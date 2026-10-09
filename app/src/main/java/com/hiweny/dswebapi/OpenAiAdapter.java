package com.hiweny.dswebapi;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.UUID;
import java.util.regex.Matcher;

/** OpenAI 兼容协议的请求/响应转换与工具调用解析（与桌面端 Core/OpenAiAdapter.cs 对齐）。 */
public final class OpenAiAdapter {

    private OpenAiAdapter() {}

    public static final String START = "<|tool\u2581calls\u2581begin|>";
    public static final String END = "<|tool\u2581calls\u2581end|>";
    private static final String START_NORM = "<|tool_calls_begin|>";
    private static final String END_NORM = "<|tool_calls_end|>";

    public static class ChatResult {
        public String content = "";
        public String thinking = "";
        public JSONArray toolCalls = null;
        public String finishReason = "stop";
        /** 工具块存在但解析失败时置位，避免把标记泄漏到正文。 */
        public String toolError = "";
        /** 已被摘除（判定为工具调用 / 写坏的调用块）的区间，供流式收尾补发时裁掉。 */
        public List<int[]> droppedSpans = null;
    }

    /** 标签归一化（1:1 字符映射，长度不变，索引可对齐）：｜->|，▁->_ */
    public static String normTag(String s) {
        if (s == null) return "";
        return s.replace('\uFF5C', '|').replace('\u2581', '_');
    }

    public static ChatResult process(String thinkingText, String contentText) {
        return process(thinkingText, contentText, null);
    }

    public static ChatResult process(String thinkingText, String contentText, java.util.Set<String> allowedNames) {
        return process(thinkingText, contentText, allowedNames, null);
    }

    /**
     * 从模型输出文本中解析工具调用；返回的 content 为剔除工具块后的正文。
     *
     * <p>只有「结构合法、且工具名来自本次请求声明的列表」的调用才生效；
     * 块「像调用却用不了」（JSON 写坏 / 被截断 / 名字写错）一律<b>整块摘除</b>，
     * 绝不把坏 JSON 留在正文里 —— 这正是「工具调用漏进正文」的根因。
     *
     * @param tools 本次请求声明的工具定义（可为 null）
     */
    public static ChatResult process(String thinkingText, String contentText, java.util.Set<String> allowedNames,
                                     JSONArray tools) {
        ChatResult r = new ChatResult();
        r.thinking = thinkingText == null ? "" : thinkingText;
        String content = contentText == null ? "" : contentText;
        if (content.isEmpty()) return r;

        boolean hasList = allowedNames != null && !allowedNames.isEmpty();

        // ① 候选块 A：**带标记**的工具块（宽容正则，含只有 begin 没有 end 的截断块）
        List<int[]> blocks = new ArrayList<int[]>();
        List<String> inners = new ArrayList<String>();
        int pos = 0;
        while (pos < content.length()) {
            Matcher mb = ToolMarkup.BEGIN.matcher(content);
            if (!mb.find(pos)) break;
            int start = mb.start();
            int afterBegin = mb.end();
            Matcher me = ToolMarkup.END.matcher(content);
            int endEx;
            String inner;
            if (me.find(afterBegin)) { endEx = me.end(); inner = content.substring(afterBegin, me.start()); }
            else { endEx = content.length(); inner = content.substring(afterBegin); }
            blocks.add(new int[]{start, endEx});
            inners.add(inner);
            pos = endEx;
        }

        // ② 候选块 B：**没有标记的裸 JSON**（模型漏写标记时的兜底）。仅当客户端确实声明了工具时才启用。
        if (hasList && blocks.isEmpty()) {
            for (int[] sp : findBareJsonSpans(content)) {
                blocks.add(sp);
                inners.add(content.substring(sp[0], sp[1]));
            }
        }

        JSONArray calls = new JSONArray();
        List<int[]> drop = new ArrayList<int[]>();   // 必须从正文里摘掉的区间
        boolean badCall = false;                      // 存在「像调用但解析不出来」的块
        for (int i = 0; i < blocks.size(); i++) {
            String inner = inners.get(i);
            boolean inFence = inCodeFence(content, blocks.get(i)[0]);
            boolean looksCall = ToolMarkup.looksLikeToolCallJson(inner);
            JSONArray raw = parseToolCalls(inner);
            JSONArray parsed = filterCalls(raw, allowedNames);
            // 没有声明工具列表时无法按名字甄别：代码块里的「示例」一律按正文处理
            boolean accepted = parsed != null && parsed.length() > 0 && !(!hasList && inFence);
            if (accepted) {
                for (int k = 0; k < parsed.length(); k++) calls.put(parsed.opt(k));
                drop.add(blocks.get(i));
            } else if (looksCall) {
                boolean parseFailed = raw == null || raw.length() == 0;
                boolean realNameNotAllowed = !parseFailed && hasList && hasRealLookingName(raw);
                if (parseFailed || realNameNotAllowed) {
                    // ★ 关键修复：块「像工具调用」却用不了 → 整块摘除（旧实现把坏 JSON 留在正文＝泄漏根因）
                    drop.add(blocks.get(i));
                    badCall = true;
                    Util.log("工具调用块不可用，已丢弃以免泄漏: " + brief(inner));
                }
                // 否则：正文里举例说明（名字是占位符，如「工具名」）→ 原样保留
            }
        }

        if (calls.length() == 0) {
            r.content = ToolMarkup.stripStray(ToolMarkup.removeSpans(content, drop));
            r.droppedSpans = drop;
            if (badCall) {
                r.toolError = "TOOL_PARSE_FAILED";
                if (r.content.trim().isEmpty()) r.content = "[工具调用格式异常，已丢弃以免泄漏到正文，请重试]";
            } else if (!blocks.isEmpty() || ToolMarkup.earliestSignal(content) >= 0) {
                r.toolError = "NOT_A_TOOL_CALL";
            }
            return r;
        }

        // ③ 有有效调用：所有候选块（含写坏的那个）一律摘掉，避免任何残片漏网
        r.content = ToolMarkup.stripStray(ToolMarkup.removeSpans(content, blocks)).trim();
        r.droppedSpans = blocks;
        ToolArgsFixer.fix(calls, tools);   // ★ 按声明类型归一化参数（string 参数不得传对象）
        r.toolCalls = calls;
        r.finishReason = "tool_calls";
        return r;
    }

    /** 解析结果里是否存在「像真工具名」的名字（区分写错名的调用与正文占位符举例）。 */
    private static boolean hasRealLookingName(JSONArray raw) {
        if (raw == null) return false;
        for (int i = 0; i < raw.length(); i++) {
            JSONObject o = raw.optJSONObject(i);
            if (o != null && looksLikeToolName(o.optString("name", "").trim())) return true;
        }
        return false;
    }

    private static String brief(String s) {
        if (s == null) return "";
        s = s.replace("\r", " ").replace("\n", " ");
        return s.length() <= 160 ? s : s.substring(0, 160) + "...";
    }

    /** 定位「没有标记的裸 JSON 工具调用块」的区间（配平扫描；截断则吃到文末）。 */
    private static List<int[]> findBareJsonSpans(String content) {
        List<int[]> out = new ArrayList<int[]>();
        Matcher m = ToolMarkup.BARE_JSON_SIG.matcher(content);
        while (m.find()) {
            int start = m.start();
            if (!out.isEmpty() && start < out.get(out.size() - 1)[1]) continue;   // 与上一块重叠
            int end = JsonRepair.balancedEnd(content, start);
            if (end < 0) end = content.length();                                   // 截断 → 吃到文末
            out.add(new int[]{start, end});
            if (out.size() >= 4) break;
        }
        return out;
    }

    /** 把「绝对区间」裁到 tail（起始于 offset）内并移除 —— 流式收尾补发时用。 */
    public static String cutSpans(String tail, int offset, List<int[]> spans) {
        if (tail == null || tail.isEmpty() || spans == null || spans.isEmpty()) return tail;
        List<int[]> rel = new ArrayList<int[]>();
        for (int[] sp : spans) {
            int s0 = Math.max(sp[0], offset) - offset;
            int e0 = Math.min(sp[1], offset + tail.length()) - offset;
            if (e0 > s0) rel.add(new int[]{s0, e0});
        }
        return ToolMarkup.removeSpans(tail, rel);
    }

    /** 从文本里移除若干 [start, end) 区间，拼接剩余部分。 */
    private static String removeRanges(String text, List<int[]> ranges) {
        if (ranges.isEmpty()) return text;
        StringBuilder sb = new StringBuilder();
        int cur = 0;
        for (int[] rg : ranges) {
            int s0 = rg[0], e0 = rg[1];
            if (s0 > cur) sb.append(text, cur, s0);
            if (e0 > cur) cur = e0;
        }
        if (cur < text.length()) sb.append(text, cur, text.length());
        return sb.toString();
    }

    /** 索引处是否位于 markdown 代码围栏（```）内部。 */
    private static boolean inCodeFence(String content, int idx) {
        int count = 0, i = -1;
        while (true) {
            i = content.indexOf("```", i + 1);
            if (i < 0 || i >= idx) break;
            count++;
        }
        return (count % 2) == 1;
    }

    /**
     * 只保留「结构完整（有 name）」且「名字确实在本次声明的工具列表里」的调用。
     * 列表为空时：占位名（含空白 / 中日韩文字，如「工具名」）不算真实工具名。
     */
    private static JSONArray filterCalls(JSONArray calls, java.util.Set<String> allowedNames) {
        if (calls == null) return null;
        boolean hasList = allowedNames != null && !allowedNames.isEmpty();
        JSONArray out = new JSONArray();
        for (int i = 0; i < calls.length(); i++) {
            JSONObject c = calls.optJSONObject(i);
            if (c == null) continue;
            String name = c.optString("name", "").trim();
            if (name.isEmpty()) continue;
            if (hasList) {
                boolean hit = false;
                for (String n : allowedNames) {
                    if (n != null && n.trim().equalsIgnoreCase(name)) { hit = true; break; }
                }
                if (!hit) continue;
            } else if (!looksLikeToolName(name)) {
                continue;
            }
            out.put(c);
        }
        return out;
    }

    /** 真实工具名不会包含空白或中日韩文字（占位符如「工具名 / 工具」会被排除）。 */
    static boolean looksLikeToolName(String n) {
        if (n == null || n.isEmpty() || n.length() > 128) return false;
        for (int i = 0; i < n.length(); i++) {
            char c = n.charAt(i);
            if (Character.isWhitespace(c)) return false;
            if ((c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF)
                    || (c >= 0xFF00 && c <= 0xFFEF) || (c >= 0x3000 && c <= 0x303F)) return false;
        }
        return true;
    }

    /**
     * 多候选解析：原文 → 结构补括号 → 全角归一 → 全角归一+补括号。
     * 顺序有讲究：不改动内容的候选优先，改动越大的越靠后。
     */
    public static JSONArray parseToolCalls(String inner) {
        if (inner == null) return null;
        String s = inner.trim();
        // 去掉可能的代码围栏
        s = s.replaceAll("^(?s)```[a-zA-Z0-9]*\\s*", "").replaceAll("(?s)```\\s*$", "").trim();
        if (s.isEmpty()) return null;

        List<String> cands = new ArrayList<String>();
        cands.add(s);
        String rb = JsonRepair.tryRebuild(s);
        if (rb != null && !rb.equals(s)) cands.add(rb);
        String fw = JsonRepair.normalizeFullWidth(s);
        if (!fw.equals(s)) {
            cands.add(fw);
            String rb2 = JsonRepair.tryRebuild(fw);
            if (rb2 != null && !rb2.equals(fw)) cands.add(rb2);
        }
        for (String cand : cands) {
            JSONArray res = parseCore(cand);
            // ⚠️ 必须校验「名字健全」：宽松解析器对未修复的原文也能吐出乱码调用名，
            //    若直接返回会短路掉后面真正能修好的候选。
            if (res != null && res.length() > 0 && namesSane(res)) return res;
        }
        return null;
    }

    /** 调用名是否「像真的工具名」（拦掉宽松解析产生的乱码名）。 */
    private static boolean namesSane(JSONArray calls) {
        if (calls == null) return false;
        for (int i = 0; i < calls.length(); i++) {
            JSONObject o = calls.optJSONObject(i);
            if (o == null) return false;
            String n = o.optString("name", "").trim();
            if (n.isEmpty() || n.length() > 128) return false;
            for (int j = 0; j < n.length(); j++) {
                char c = n.charAt(j);
                if (Character.isWhitespace(c) || c == '"' || c == '\'' || c == '\\' || c == '{' || c == '}'
                        || c == '[' || c == ']' || c == ',' || c == '\uFF1A' || c == '\uFF0C') return false;
            }
        }
        return true;
    }

    /** 对**单一候选文本**多级解析（严格 → 宽松 → 逐对象提取）。 */
    private static JSONArray parseCore(String s) {
        // 1) 严格解析：整段 JSON 数组
        int lb = s.indexOf('['), rb = s.lastIndexOf(']');
        if (lb >= 0 && rb > lb) {
            JSONArray a = tryArray(s.substring(lb, rb + 1));
            if (a != null) { JSONArray nn = normalizeCalls(a); if (nn.length() > 0) return nn; }
        }
        // 2) 严格解析：整段文本 / 单个对象（含 {"tool_calls":[…]} 包装）
        JSONArray a2 = tryArray(s);
        if (a2 != null) { JSONArray nn = normalizeCalls(a2); if (nn.length() > 0) return nn; }
        JSONObject o2 = tryObject(s);
        if (o2 != null) { JSONArray nn = wrapCalls(o2); if (nn.length() > 0) return nn; }

        // 3) 宽松解析：修复「嵌套 / 字符串化 JSON」的多层转义错误（必须排在 extractObjects 之前）
        JSONArray loose = normalizeLoose(LooseJson.parse(s));
        if (loose != null && loose.length() > 0) return loose;

        // 4) 最后的兜底：逐个提取 {...} 片段
        for (String ch : extractObjects(s)) {
            JSONObject o = tryObject(ch);
            if (o != null) { JSONArray nn = wrapCalls(o); if (nn.length() > 0) return nn; }
            JSONArray n2 = normalizeLoose(LooseJson.parse(ch));
            if (n2 != null && n2.length() > 0) return n2;
        }
        return null;
    }

    private static JSONArray wrapCalls(JSONObject o) {
        // {"tool_calls":[…]} / {"function_calls":[…]} 这类**包装层**：直接取内层数组
        String[] keys = {"tool_calls", "tool_call", "function_calls", "calls"};
        for (String k : keys) {
            JSONArray arr = o.optJSONArray(k);
            if (arr != null) {
                JSONArray nn0 = normalizeCalls(arr);
                if (nn0.length() > 0) return nn0;
            }
        }
        JSONArray a = new JSONArray();
        a.put(o);
        return normalizeCalls(a);
    }

    private static JSONArray normalizeLoose(Object v) {
        if (v instanceof JSONArray) return normalizeCalls((JSONArray) v);
        if (v instanceof JSONObject) return wrapCalls((JSONObject) v);
        return null;
    }

    private static JSONArray tryArray(String s) {
        try { return new JSONArray(s); } catch (Exception e) { return null; }
    }
    private static JSONObject tryObject(String s) {
        try { return new JSONObject(s); } catch (Exception e) { return null; }
    }

    /** 统一为 [{name, arguments}]（arguments 为对象，必要时经宽松解析修复）。 */
    private static JSONArray normalizeCalls(JSONArray a) {
        JSONArray out = new JSONArray();
        for (int i = 0; i < a.length(); i++) {
            JSONObject item = a.optJSONObject(i);
            if (item == null) continue;
            String name = firstString(item, "name", "tool_name", "tool");
            if (name.isEmpty()) {
                JSONObject fn = item.optJSONObject("function");
                if (fn != null) name = firstString(fn, "name", "tool_name", "tool");
            }
            if (name.isEmpty()) continue;
            Object args = item.opt("arguments");
            if (args == null) args = item.opt("parameters");
            if (args == null) args = item.opt("args");
            if (args == null) args = item.opt("input");
            if (args == null) {
                JSONObject fn = item.optJSONObject("function");
                if (fn != null) args = fn.opt("arguments");
            }
            JSONObject call = new JSONObject();
            try {
                call.put("name", name);
                putArgs(call, args);
            } catch (Exception ignored) {}
            out.put(call);
        }
        return out;
    }

    private static String firstString(JSONObject o, String... keys) {
        for (String k : keys) {
            String v = o.optString(k, "").trim();
            if (!v.isEmpty()) return v;
        }
        return "";
    }

    /** 各种形态的 arguments 统一成「对象」；实在解析不出则把原始文本存入 _raw_args，绝不静默丢弃。 */
    private static void putArgs(JSONObject call, Object args) throws Exception {
        // 模型把 arguments 套成了一层数组 [{…}]（实测：arguments 写成数组后 ]/} 顺序错乱）→ 取唯一元素
        if (args instanceof JSONArray) {
            JSONArray arr = (JSONArray) args;
            if (arr.length() == 1 && arr.opt(0) instanceof JSONObject) args = arr.opt(0);
        }
        if (args instanceof JSONObject || args instanceof JSONArray) {
            call.put("arguments", LooseJson.normalizeJsonStrings(args));
            return;
        }
        String as = args == null ? "" : String.valueOf(args).trim();
        if (as.isEmpty() || "{}".equals(as)) { call.put("arguments", new JSONObject()); return; }
        JSONObject ao = tryObject(as);
        if (ao != null) { call.put("arguments", LooseJson.normalizeJsonStrings(ao)); return; }
        Object lo = LooseJson.parse(as);
        if (lo instanceof JSONObject || lo instanceof JSONArray) { call.put("arguments", LooseJson.normalizeJsonStrings(lo)); return; }
        call.put("arguments", new JSONObject());
        call.put("_raw_args", as);
    }

    /** 扫描出顶层 {...} 片段（简易括号计数，忽略字符串内括号）。 */
    private static List<String> extractObjects(String s) {
        List<String> out = new ArrayList<String>();
        int depth = 0, start = -1;
        boolean inStr = false;
        char q = 0;
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (inStr) {
                if (c == '\\') { i++; continue; }
                if (c == q) inStr = false;
                continue;
            }
            if (c == '"' || c == '\'') { inStr = true; q = c; continue; }
            if (c == '{') { if (depth == 0) start = i; depth++; }
            else if (c == '}') { depth--; if (depth == 0 && start >= 0) { out.add(s.substring(start, i + 1)); start = -1; } }
        }
        return out;
    }

    /* ================= 响应构建 ================= */

    public static String newId() { return "chatcmpl-" + UUID.randomUUID().toString().replace("-", "").substring(0, 24); }

    public static int estTokens(String s) {
        if (s == null || s.isEmpty()) return 0;
        // 粗略估计：中文≈1.6字/token，英文≈4字符/token。取中值近似。
        return Math.max(1, (int) Math.ceil(s.length() / 2.0));
    }

    public static JSONObject buildMessage(String thinking, String content, JSONArray toolCalls) {
        JSONObject msg = new JSONObject();
        try {
            msg.put("role", "assistant");
            if (toolCalls != null && toolCalls.length() > 0) {
                msg.put("content", content == null || content.isEmpty() ? JSONObject.NULL : content);
                msg.put("tool_calls", toOpenAiToolCalls(toolCalls));
            } else {
                msg.put("content", content == null ? "" : content);
            }
            if (thinking != null && !thinking.isEmpty()) msg.put("reasoning_content", thinking);
        } catch (Exception ignored) {}
        return msg;
    }

    public static JSONArray toOpenAiToolCalls(JSONArray calls) {
        JSONArray out = new JSONArray();
        for (int i = 0; i < calls.length(); i++) {
            JSONObject c = calls.optJSONObject(i);
            if (c == null) continue;
            String name = c.optString("name");
            String argStr = argStringOf(c);
            JSONObject tc = new JSONObject();
            JSONObject fn = new JSONObject();
            try {
                fn.put("name", name);
                fn.put("arguments", argStr);
                tc.put("id", "call_" + UUID.randomUUID().toString().replace("-", "").substring(0, 22));
                tc.put("type", "function");
                tc.put("function", fn);
            } catch (Exception ignored) {}
            out.put(tc);
        }
        return out;
    }

    /**
     * 生成 OpenAI 要求的 arguments 字符串。
     * 无论模型/上游给的是对象还是「字符串化的 JSON」，这里都会归一化为**合法 JSON 文本**，
     * 从而彻底消除多层转义（「\\" vs \"）在下游客户端导致的解析失败。
     */
    private static String argStringOf(JSONObject c) {
        Object args = c.opt("arguments");
        String raw = c.optString("_raw_args", "");
        String text;
        if (args instanceof JSONObject && ((JSONObject) args).length() == 0 && !raw.isEmpty()) text = raw;
        else if (args == null) text = raw;
        else if (args instanceof String) text = (String) args;
        else text = args.toString();
        String t = text == null ? "" : text.trim();
        if (t.isEmpty()) return "{}";
        // 合法 JSON 直接用严格解析（更快、更稳），只有严格解析失败时才走宽松修复
        try { return new JSONObject(t).toString(); } catch (Exception ignored) { }
        try { return new JSONArray(t).toString(); } catch (Exception ignored) { }
        Object parsed = LooseJson.parse(t);
        if (parsed instanceof JSONObject) return ((JSONObject) parsed).toString();
        if (parsed instanceof JSONArray) return ((JSONArray) parsed).toString();
        return t;
    }

    public static JSONObject buildCompletion(String id, String model, long created,
                                             String thinking, String content, JSONArray toolCalls, String finishReason,
                                             int promptTokens, int completionTokens) {
        JSONObject o = new JSONObject();
        try {
            o.put("id", id);
            o.put("object", "chat.completion");
            o.put("created", created);
            o.put("model", model);
            JSONArray choices = new JSONArray();
            JSONObject ch = new JSONObject();
            ch.put("index", 0);
            ch.put("message", buildMessage(thinking, content, toolCalls));
            ch.put("finish_reason", finishReason);
            choices.put(ch);
            o.put("choices", choices);
            JSONObject usage = new JSONObject();
            usage.put("prompt_tokens", promptTokens);
            usage.put("completion_tokens", completionTokens);
            usage.put("total_tokens", promptTokens + completionTokens);
            o.put("usage", usage);
        } catch (Exception ignored) {}
        return o;
    }

    /** 生成一条 SSE chunk（不含 "data: " 前缀）。 */
    public static String chunk(String id, String model, long created, JSONObject delta, String finishReason) {
        JSONObject o = new JSONObject();
        try {
            o.put("id", id);
            o.put("object", "chat.completion.chunk");
            o.put("created", created);
            o.put("model", model);
            JSONArray choices = new JSONArray();
            JSONObject ch = new JSONObject();
            ch.put("index", 0);
            ch.put("delta", delta);
            ch.put("finish_reason", finishReason == null ? JSONObject.NULL : finishReason);
            choices.put(ch);
            o.put("choices", choices);
        } catch (Exception ignored) {}
        return o.toString();
    }

    public static JSONObject modelsList() {
        JSONObject o = new JSONObject();
        try {
            o.put("object", "list");
            JSONArray data = new JSONArray();
            data.put(model("deepseek", "DeepSeek (web)"));
            data.put(model("deepseek-chat", "DeepSeek Chat (alias)"));
            data.put(model("deepseek-reasoner", "DeepSeek Reasoner (alias)"));
            o.put("data", data);
        } catch (Exception ignored) {}
        return o;
    }

    private static JSONObject model(String id, String desc) {
        JSONObject m = new JSONObject();
        try { m.put("id", id); m.put("object", "model"); m.put("created", 1700000000); m.put("owned_by", "deepseek-web"); } catch (Exception ignored) {}
        return m;
    }
}
