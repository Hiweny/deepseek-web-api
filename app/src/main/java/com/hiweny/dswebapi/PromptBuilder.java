package com.hiweny.dswebapi;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

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
    }

    private PromptBuilder() {}

    public static Result build(JSONObject req, boolean stateless) {
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
            sb.append("  - 调用方法: `").append(TOOL_START).append("[{\"name\": \"").append(name)
              .append("\", \"arguments\": ").append(params).append("}]").append(TOOL_END).append("`\n");
            sb.append("  - 简要说明:\n~~~markdown\n  ").append(desc.isEmpty() ? "无描述" : desc).append("\n~~~\n");
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
        sb.append("10. 严禁在正文、思考、代码块或示例中原样写出工具调用标记本身。"
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
