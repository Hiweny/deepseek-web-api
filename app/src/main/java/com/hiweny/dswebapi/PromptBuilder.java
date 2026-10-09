package com.hiweny.dswebapi;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/**
 * 把 OpenAI Chat Completions 请求转换为「注入到官网输入框」的文本，并提取附件。
 *
 * 会话模式（默认）：只发送本轮新增内容（最后一条 assistant 之后的消息），
 * 依赖官网自身会话维持多轮上下文；系统指令与工具定义以自然语言块注入。
 * 无状态模式：把全部消息按 ChatML 角色标签拼接（参考 ds-free-api）。
 */
public final class PromptBuilder {

    public static final String TOOL_START = "<|tool\u2581calls\u2581begin|>";
    public static final String TOOL_END = "<|tool\u2581calls\u2581end|>";
    public static final String TOOL_OUT_B = "<\uFF5Ctool\u2581outputs\u2581begin\uFF5C><\uFF5Ctool\u2581output\u2581begin\uFF5C>";
    public static final String TOOL_OUT_E = "<\uFF5Ctool\u2581output\u2581end\uFF5C><\uFF5Ctool\u2581outputs\u2581end\uFF5C>";

    public static class Attachment {
        public final String name, mime, base64;
        public Attachment(String name, String mime, String base64) { this.name = name; this.mime = mime; this.base64 = base64; }
    }

    public static class Result {
        public String text = "";
        public final List<Attachment> attachments = new ArrayList<>();
        public boolean hasTools = false;
        /** 本次请求声明的工具名（用于甄别「正文里解释标记」的误判）。 */
        public final List<String> toolNames = new ArrayList<>();
        /** 本次因超长被中段截断（正文里已插省略标记）。 */
        public boolean truncated = false;
        /** 截断时被省略的字符数。 */
        public long omittedChars = 0;
        /** 因超出附件数量上限被略过的图片数。 */
        public int omittedImages = 0;
    }

    private PromptBuilder() {}

    public static Result build(JSONObject req, boolean stateless) { return build(req, stateless, 0, 0); }

    /**
     * 按上限构建 prompt：
     *   maxChars &gt; 0     → 超长时「中段截断」（保留系统指令/工具协议 + 最近对话）
     *   maxRefImages &gt; 0 → 图片附件只保留最近 N 张
     * 目的是避免前端把超长工具结果塞进下一轮，撞上网页端「输入上限 / 附件数量上限」而报调用错误。
     */
    public static Result build(JSONObject req, boolean stateless, int maxChars, int maxRefImages) {
        Result r = buildCore(req, stateless);
        fit(r, maxChars, maxRefImages);
        return r;
    }

    private static Result buildCore(JSONObject req, boolean stateless) {
        Result r = new Result();
        JSONArray messages = req.optJSONArray("messages");
        if (messages == null || messages.length() == 0) { r.text = ""; return r; }

        JSONArray tools = req.optJSONArray("tools");
        r.hasTools = tools != null && tools.length() > 0;
        collectToolNames(tools, r.toolNames);

        String toolBlock = r.hasTools ? buildToolBlock(tools, req) : "";
        String formatBlock = responseFormatBlock(req.optJSONObject("response_format"));

        if (stateless) {
            r.text = buildStateless(messages, toolBlock, formatBlock, r);
            return r;
        }

        // 会话模式：定位 delta 起点（最后一条 assistant 之后）
        int deltaStart = 0;
        for (int i = messages.length() - 1; i >= 0; i--) {
            JSONObject m = messages.optJSONObject(i);
            if (m != null && "assistant".equals(m.optString("role"))) { deltaStart = i + 1; break; }
        }

        StringBuilder sb = new StringBuilder();
        StringBuilder sysBuf = new StringBuilder();
        StringBuilder body = new StringBuilder();

        for (int i = deltaStart; i < messages.length(); i++) {
            JSONObject m = messages.optJSONObject(i);
            if (m == null) continue;
            String role = m.optString("role");
            if ("system".equals(role)) {
                String t = contentToText(m.opt("content"), r, false);
                if (!t.isEmpty()) { if (sysBuf.length() > 0) sysBuf.append("\n\n"); sysBuf.append(t); }
            } else if ("user".equals(role)) {
                String t = contentToText(m.opt("content"), r, true);
                if (!t.isEmpty()) { if (body.length() > 0) body.append("\n\n"); body.append(t); }
            } else if ("assistant".equals(role)) {
                appendAssistant(m, body, r);
            } else if ("tool".equals(role) || "function".equals(role)) {
                String t = contentToText(m.opt("content"), r, false);
                if (body.length() > 0) body.append("\n\n");
                body.append(TOOL_OUT_B).append(t).append(TOOL_OUT_E);
            }
        }

        boolean hasSysContent = sysBuf.length() > 0;
        if (hasSysContent || !toolBlock.isEmpty() || !formatBlock.isEmpty()) {
            sb.append("\u3010系统指令\u3011\n");
            if (hasSysContent) sb.append(sysBuf);
            if (!toolBlock.isEmpty()) { if (hasSysContent) sb.append("\n\n"); sb.append(toolBlock); }
            if (!formatBlock.isEmpty()) { sb.append("\n\n").append(formatBlock); }
            sb.append("\n\n");
        }
        sb.append(body);
        r.text = sb.toString().trim();
        return r;
    }

    /** 上限保护：附件只留最近 N 张；prompt 超长则中段截断（保留系统指令/工具协议与最近对话）。 */
    private static void fit(Result r, int maxChars, int maxRefImages) {
        // ① 附件数量上限：网页端对一批引用的数量有硬上限，越过会让该会话此后每一轮都失败
        if (maxRefImages > 0 && r.attachments.size() > maxRefImages) {
            r.omittedImages = r.attachments.size() - maxRefImages;
            for (int i = 0; i < r.omittedImages; i++) r.attachments.remove(0);   // 丢弃最早的
        }
        // ② 字符预算：中段截断
        if (maxChars > 0) fitChars(r, maxChars);
    }

    private static void fitChars(Result r, int maxChars) {
        String text = r.text == null ? "" : r.text;
        if (text.length() <= maxChars) return;
        final int RESERVE = 160;                       // 省略标记预留
        long budget = Math.max(512, maxChars - RESERVE);

        // head = 从「【系统指令】」到第一个空行（系统提示 + 工具协议），尽量完整保留，避免工具 schema 被截半
        String head = "";
        String body = text;
        if (text.startsWith("\u3010系统指令\u3011")) {
            int cut = text.indexOf("\n\n", 6);
            if (cut > 0) { head = text.substring(0, cut); body = text.substring(cut + 2); }
        }
        long headBudget = Math.min(head.length(), (long) Math.floor(budget * 0.45));
        long bodyBudget = budget - headBudget;
        if (bodyBudget < 0) bodyBudget = 0;

        String headOut = head.length() > headBudget ? head.substring(0, (int) headBudget) : head;
        String bodyOut = body;
        if (body.length() > bodyBudget) {
            int keep = (int) bodyBudget;
            int start = body.length() - keep;               // 保留尾部（最近回合）
            int nl = body.indexOf("\n\n", start);
            if (nl >= 0 && nl - start <= 800) start = nl + 2;
            else { int nl2 = body.indexOf('\n', start); if (nl2 >= 0 && nl2 - start <= 240) start = nl2 + 1; }
            if (start < 0) start = 0;
            if (start > body.length()) start = body.length();
            bodyOut = body.substring(start);
        }
        long omitted = (head.length() - headOut.length()) + (body.length() - bodyOut.length());
        StringBuilder sb = new StringBuilder();
        if (headOut.length() > 0) sb.append(headOut).append("\n\n");
        sb.append("[上下文过长：已自动省略约 ").append(omitted).append(" 字符，仅保留系统指令与最近对话]\n\n");
        sb.append(bodyOut);
        r.text = sb.toString().trim();
        r.truncated = true;
        r.omittedChars = omitted;
    }

    private static String buildStateless(JSONArray messages, String toolBlock, String formatBlock, Result r) {
        StringBuilder sb = new StringBuilder();
        StringBuilder sys = new StringBuilder();
        for (int i = 0; i < messages.length(); i++) {
            JSONObject m = messages.optJSONObject(i);
            if (m == null) continue;
            if ("system".equals(m.optString("role"))) {
                String t = contentToText(m.opt("content"), r, false);
                if (!t.isEmpty()) { if (sys.length() > 0) sys.append("\n\n"); sys.append(t); }
            }
        }
        String sysAll = sys.toString().trim();
        StringBuilder body = new StringBuilder();
        if (!sysAll.isEmpty()) body.append("\u3010系统指令\u3011\n").append(sysAll);
        if (!toolBlock.isEmpty()) { if (body.length() > 0) body.append("\n\n"); body.append(toolBlock); }
        if (!formatBlock.isEmpty()) { if (body.length() > 0) body.append("\n\n"); body.append(formatBlock); }
        if (body.length() > 0) body.append("\n\n");

        for (int i = 0; i < messages.length(); i++) {
            JSONObject m = messages.optJSONObject(i);
            if (m == null) continue;
            String role = m.optString("role");
            if ("system".equals(role)) continue;
            if ("assistant".equals(role)) appendAssistant(m, body, r);
            else if ("tool".equals(role) || "function".equals(role)) {
                String t = contentToText(m.opt("content"), r, false);
                if (body.length() > 0) body.append("\n\n");
                body.append(TOOL_OUT_B).append(t).append(TOOL_OUT_E);
            } else {
                String t = contentToText(m.opt("content"), r, true);
                if (!t.isEmpty()) { if (body.length() > 0) body.append("\n\n"); body.append(t); }
            }
        }
        return body.toString().trim();
    }

    private static void appendAssistant(JSONObject m, StringBuilder body, Result r) {
        String t = contentToText(m.opt("content"), r, false);
        JSONArray calls = m.optJSONArray("tool_calls");
        StringBuilder s = new StringBuilder();
        if (!t.isEmpty()) s.append(t);
        if (calls != null && calls.length() > 0) {
            StringBuilder arr = new StringBuilder();
            for (int i = 0; i < calls.length(); i++) {
                JSONObject tc = calls.optJSONObject(i);
                JSONObject fn = tc == null ? null : tc.optJSONObject("function");
                if (fn == null) continue;
                if (arr.length() > 0) arr.append(", ");
                arr.append("{\"name\": ").append(JSONObject.quote(fn.optString("name"))).append(", \"arguments\": ")
                   .append(safeArgs(fn.optString("arguments"))).append("}");
            }
            if (arr.length() > 0) s.append(TOOL_START).append("[").append(arr).append("]").append(TOOL_END);
        }
        if (s.length() > 0) {
            if (body.length() > 0) body.append("\n\n");
            body.append(s);
        }
    }

    private static String safeArgs(String args) {
        String a = args == null ? "" : args.trim();
        if (a.isEmpty()) return "{}";
        try { new JSONObject(a); return a; } catch (Exception e) { return JSONObject.quote(a); }
    }

    /** content 可能是字符串，也可能是 parts 数组；text 部分拼文本，image/file 部分收集为附件。 */
    private static String contentToText(Object content, Result r, boolean collectImages) {
        if (content == null) return "";
        if (content instanceof String) return ((String) content).trim();
        if (!(content instanceof JSONArray)) return "";
        JSONArray parts = (JSONArray) content;
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < parts.length(); i++) {
            JSONObject p = parts.optJSONObject(i);
            if (p == null) { continue; }
            String type = p.optString("type");
            if ("text".equals(type) || "input_text".equals(type)) {
                if (sb.length() > 0) sb.append('\n');
                sb.append(p.optString("text"));
            } else if ("image_url".equals(type)) {
                JSONObject iu = p.optJSONObject("image_url");
                String url = iu != null ? iu.optString("url") : p.optString("image_url");
                Attachment a = toAttachment(url, "image");
                if (a != null) { r.attachments.add(a); if (collectImages) sb.append("[已附加图片 ").append(a.name).append("]"); }
            } else if ("input_image".equals(type)) {
                String url = p.optString("image_url");
                Attachment a = toAttachment(url, "image");
                if (a != null) { r.attachments.add(a); if (collectImages) sb.append("[已附加图片 ").append(a.name).append("]"); }
            } else if ("file".equals(type) || "input_file".equals(type)) {
                JSONObject f = p.optJSONObject("file");
                if (f == null) f = p;
                String name = f.optString("filename", f.optString("name", "file"));
                String data = f.optString("file_data", f.optString("data", ""));
                if (data.startsWith("data:")) {
                    DataUrl du = DataUrl.parse(data);
                    if (du != null) { r.attachments.add(new Attachment(name, du.mime, du.b64)); sb.append("[已附加文件 ").append(name).append("]"); }
                } else if (data.startsWith("http")) {
                    Attachment a = Downloader.fetch(data, name);
                    if (a != null) { r.attachments.add(a); sb.append("[已附加文件 ").append(name).append("]"); }
                }
            }
        }
        return sb.toString().trim();
    }

    private static Attachment toAttachment(String url, String kind) {
        if (url == null || url.isEmpty()) return null;
        if (url.startsWith("data:")) {
            DataUrl du = DataUrl.parse(url);
            if (du == null) return null;
            String ext = du.mime.contains("png") ? ".png" : du.mime.contains("jpeg") || du.mime.contains("jpg") ? ".jpg"
                    : du.mime.contains("gif") ? ".gif" : du.mime.contains("webp") ? ".webp" : "";
            return new Attachment(kind + "-" + System.currentTimeMillis() + ext, du.mime, du.b64);
        }
        if (url.startsWith("http")) {
            return Downloader.fetch(url, kind + "-" + System.currentTimeMillis());
        }
        return null;
    }

    private static String responseFormatBlock(JSONObject rf) {
        if (rf == null) return "";
        String type = rf.optString("type", "");
        if ("json_object".equals(type)) return "请直接输出合法的 JSON 对象，不要包含 markdown 代码块标记或解释性文字。";
        if ("json_schema".equals(type)) {
            JSONObject js = rf.optJSONObject("json_schema");
            return "请以 JSON 形式输出，并遵守以下 JSON Schema：\n" + (js == null ? "{}" : js.toString());
        }
        return "";
    }

    /** 收集工具名（兼容 function.name 与顶层 name 两种形态）。 */
    private static void collectToolNames(JSONArray tools, List<String> out) {
        if (tools == null) return;
        for (int i = 0; i < tools.length(); i++) {
            JSONObject t = tools.optJSONObject(i);
            if (t == null) continue;
            JSONObject fn = t.optJSONObject("function");
            String n = (fn != null ? fn.optString("name", "") : t.optString("name", "")).trim();
            if (!n.isEmpty() && !out.contains(n)) out.add(n);
        }
    }

    /**
     * 展开 parameters schema 为「参数清单 + 按声明类型的示例 arguments」。
     * 不再把 schema 原文直接当作示例值（那会让模型照着 schema 形状输出）。
     */
    private static String[] describeParams(String prmsJson) {
        StringBuilder sb = new StringBuilder();
        JSONObject ex = new JSONObject();
        JSONObject schema = null;
        if (prmsJson != null && !prmsJson.trim().isEmpty()) {
            try { schema = new JSONObject(prmsJson); } catch (Exception ignored) { }
            if (schema == null) {
                Object v = LooseJson.parse(prmsJson);
                if (v instanceof JSONObject) schema = (JSONObject) v;
            }
        }
        JSONObject props = schema == null ? null : schema.optJSONObject("properties");
        Set<String> required = new HashSet<>();
        JSONArray reqArr = schema == null ? null : schema.optJSONArray("required");
        if (reqArr != null) {
            for (int i = 0; i < reqArr.length(); i++) {
                String s = reqArr.optString(i, "");
                if (!s.isEmpty()) required.add(s);
            }
        }
        if (props != null) {
            for (Iterator<String> it = props.keys(); it.hasNext(); ) {
                String k = it.next();
                JSONObject p = props.optJSONObject(k);
                Set<String> types = new HashSet<>();
                collectSchemaTypes(p, types);
                String d = p == null ? "" : p.optString("description", "").replace("\n", " ").trim();
                if (d.length() > 120) d = d.substring(0, 120) + "…";
                sb.append("    - `").append(k).append("` (").append(types.isEmpty() ? "any" : joinTypes(types)).append(")")
                  .append(required.contains(k) ? " 【必填】" : "");
                if (!d.isEmpty()) sb.append(" — ").append(d);
                sb.append("\n");
                try { ex.put(k, sampleValue(types, p)); } catch (Exception ignored) { }
            }
        }
        return new String[]{sb.toString(), ex.length() > 0 ? ex.toString() : "{}"};
    }

    private static String joinTypes(Set<String> types) {
        StringBuilder b = new StringBuilder();
        for (String t : types) {
            if (b.length() > 0) b.append("|");
            b.append(t);
        }
        return b.toString();
    }

    private static void collectSchemaTypes(JSONObject node, Set<String> out) {
        if (node == null) return;
        Object tp = node.opt("type");
        if (tp instanceof String) {
            String s = ((String) tp).trim();
            if (!s.isEmpty()) out.add(s.toLowerCase(Locale.ROOT));
        } else if (tp instanceof JSONArray) {
            JSONArray ta = (JSONArray) tp;
            for (int i = 0; i < ta.length(); i++) {
                Object x = ta.opt(i);
                if (x instanceof String) out.add(((String) x).trim().toLowerCase(Locale.ROOT));
            }
        }
        for (String key : new String[]{"anyOf", "oneOf", "allOf"}) {
            JSONArray arr = node.optJSONArray(key);
            if (arr == null) continue;
            for (int i = 0; i < arr.length(); i++) collectSchemaTypes(arr.optJSONObject(i), out);
        }
    }

    private static Object sampleValue(Set<String> types, JSONObject node) {
        JSONArray en = node == null ? null : node.optJSONArray("enum");
        if (en != null && en.length() > 0) return en.opt(0);
        if (types.contains("boolean")) return Boolean.TRUE;
        if (types.contains("integer") || types.contains("number")) return 0;
        if (types.contains("array")) return new JSONArray();
        if (types.contains("object")) return new JSONObject();
        return "字符串";
    }

    private static String buildToolBlock(JSONArray tools, JSONObject req) {
        StringBuilder sb = new StringBuilder();
        sb.append("你可以使用以下工具：\n");
        List<String> names = new ArrayList<>();
        for (int i = 0; i < tools.length(); i++) {
            JSONObject t = tools.optJSONObject(i);
            if (t == null) continue;
            JSONObject fn = t.optJSONObject("function");
            if (fn == null) continue;
            String name = fn.optString("name");
            if (name.isEmpty()) continue;
            names.add(name);
            String params = fn.optString("parameters", "{}");
            String desc = fn.optString("description", "").trim();
            sb.append("- **").append(name).append("** (function):\n");
            if (!desc.isEmpty()) {
                sb.append("  - 说明: ").append(desc.replace("\n", " ").replace("```", "[代码块]")).append("\n");
            }
            String[] dp = describeParams(params);
            sb.append("  - 参数（**必须严格按括号里声明的类型写值**）:\n");
            sb.append(dp[0].isEmpty() ? "    - 无\n" : dp[0]);
            sb.append("  - 调用示例: `").append(TOOL_START).append("[{\"name\": \"").append(name)
              .append("\", \"arguments\": ").append(dp[1]).append("}]").append(TOOL_END).append("`\n");
        }

        sb.append("\n**工具调用格式 — 请严格遵守：**\n\n");
        sb.append("将 JSON 数组包裹在工具调用标记中：\n\n");
        sb.append(TOOL_START).append("[{\"name\": \"工具名\", \"arguments\": {参数JSON}}]").append(TOOL_END).append("\n\n");
        sb.append("**规则：**\n\n");
        sb.append("1. 决定调用工具时，响应中**只允许**出现工具调用文本本身，禁止任何解释、前缀、总结、问候语。\n");
        sb.append("2. JSON 数组必须以 `").append(TOOL_START).append("` 开头、以 `").append(TOOL_END).append("` 结尾，完整包裹。\n");
        sb.append("3. 所有工具调用放在**一个** JSON 数组中，多个用逗号分隔。\n");
        sb.append("4. 输出 `").append(TOOL_END).append("` 后立即停止，不要添加后续文字。\n");
        sb.append("5. 不要用 markdown 代码块包裹工具调用。\n");
        sb.append("6. 字符串参数值用**双引号**（标准 JSON）。\n");
        sb.append("7. 不要将工具调用或最终回复放进思考内容里。\n");
        sb.append("8. `arguments` 必须是一个 **JSON 对象**，不要把它整体再写成字符串（禁止 `\"arguments\": \"{...}\"` 这种写法）。\n");
        sb.append("9. 若某个参数值本身就是一段 JSON 文本，则该值内部的双引号只需要转义**一层**，请严格照下方示例的写法，不要漏转义、也不要多转义。\n");
        sb.append("10. **严格按照参数声明的类型写值（非常重要）**：声明为 `string` 的参数，其值必须是**字符串**——"
                + "如果这个字符串的内容本身是一段 JSON，请把它整段序列化成文本（内部双引号用 `\\\"` 转义一层），"
                + "例如 `\"params\": \"{\\\"function\\\":\\\"() => 1\\\"}\"`；"
                + "声明为 `object` / `array` 的参数才写对象 / 数组。"
                + "严禁把声明为 `string` 的参数写成对象（宿主会对该字符串再解析一次，传对象会直接报错），"
                + "也严禁把声明为 `object` 的参数写成字符串。\n");
        sb.append("12. **不要使用网页自带的联网搜索**：只通过上面列出的工具获取外部信息。"
                + "网页搜索的结果会与工具调用混淆，请始终用工具调用标记来请求工具。\n");
        sb.append("11. 严禁在正文、思考、代码块或示例中原样写出工具调用标记本身。"
                + "若需要说明格式，请用「工具调用开始标记 / 结束标记」这样的文字描述；"
                + "正文里出现真实标记会被系统当成工具调用，导致你后面的内容被截断。\n");
        if (!names.isEmpty()) {
            String a = names.get(0);
            sb.append("\n**示例**（调用一个工具）：\n");
            sb.append(TOOL_START).append("[{\"name\": \"").append(a).append("\", \"arguments\": {}}]").append(TOOL_END);
            if (names.size() >= 2) {
                sb.append("\n\n**示例**（并行调用两个工具）：\n");
                sb.append(TOOL_START).append("[{\"name\": \"").append(names.get(0)).append("\", \"arguments\": {}}, {\"name\": \"")
                  .append(names.get(1)).append("\", \"arguments\": {}}]").append(TOOL_END);
            }
        }
        sb.append("\n**示例**（参数值里含 JSON 字符串时，注意只转义一层）：\n");
        sb.append(TOOL_START)
          .append("[{\"name\": \"example\", \"arguments\": {\"params\": {\"files\": \"[{\\\"field_name\\\": \\\"a.txt\\\"}]\"}}}]")
          .append(TOOL_END);
        String tc = req.optString("tool_choice", "");
        if ("required".equals(tc)) sb.append("\n\n**注意：你必须调用一个或多个工具。**");
        return sb.toString();
    }

    /** data:image/png;base64,xxxx */
    public static class DataUrl {
        public String mime, b64;
        static DataUrl parse(String s) {
            try {
                int comma = s.indexOf(',');
                if (comma < 0) return null;
                String head = s.substring(5, comma); // after "data:"
                String b64 = s.substring(comma + 1);
                String mime = head;
                int semi = head.indexOf(';');
                if (semi >= 0) mime = head.substring(0, semi);
                if (mime.isEmpty()) mime = "application/octet-stream";
                DataUrl d = new DataUrl();
                d.mime = mime;
                d.b64 = b64.trim();
                return d;
            } catch (Exception e) { return null; }
        }
    }
}
