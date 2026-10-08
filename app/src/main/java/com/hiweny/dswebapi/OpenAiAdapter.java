package com.hiweny.dswebapi;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

/** OpenAI 兼容协议的请求/响应转换与工具调用解析。 */
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
    }

    /** 标签归一化（1:1 字符映射，长度不变，索引可对齐）：｜->|，▁->_ */
    public static String normTag(String s) {
        if (s == null) return "";
        return s.replace('\uFF5C', '|').replace('\u2581', '_');
    }

    public static ChatResult process(String thinkingText, String contentText) {
        return process(thinkingText, contentText, null);
    }

    /**
     * 从模型输出文本中解析工具调用；返回的 content 为剔除工具块后的正文。
     *
     * <p>只有当标记块内确实是「结构合法、且工具名来自本次请求声明的列表」的工具调用时，
     * 才按工具调用处理；否则一律视为普通正文原样返回（不在正文里吞内容）。
     * 这样才能区分「真的调用工具」与「模型在正文里解释/举例这个标记」。
     *
     * @param allowedNames 本次请求声明的工具名集合，可为 null（表示不校验名字）
     */
    public static ChatResult process(String thinkingText, String contentText, java.util.Set<String> allowedNames) {
        return process(thinkingText, contentText, allowedNames, null);
    }

    /**
     * 增加 tools：按工具声明的 JSON Schema 归一化参数。
     *
     * <p>声明为 string 的参数，如果模型写成了对象/数组，会序列化成「只转义一层」的字符串，
     * 以适配宿主侧「再解析一次 JSON」的约定（见 {@link ToolArgsFixer}）；
     * 声明为 object/array 的参数保持原样，不会被反向改写。
     *
     * @param tools 本次请求声明的工具定义（可为 null）
     */
    public static ChatResult process(String thinkingText, String contentText, java.util.Set<String> allowedNames,
                                     JSONArray tools) {
        ChatResult r = new ChatResult();
        r.thinking = thinkingText == null ? "" : thinkingText;
        String content = contentText == null ? "" : contentText;
        if (content.isEmpty()) return r;

        // ① 用**宽容正则**扫描所有工具块（覆盖竖线半角/全角、分隔符 _/▁/空格、大小写等变体，
        //    以及只有 begin 没有 end 的截断块）。旧实现用精确 indexOf，只认一种形态，
        //    变体与残片会整段落进正文 —— 这就是「工具标记漏进正文」的根因。
        java.util.List<int[]> ranges = new java.util.ArrayList<int[]>();
        java.util.List<String> inners = new java.util.ArrayList<String>();
        int pos = 0;
        while (pos < content.length()) {
            java.util.regex.Matcher mb = ToolMarkup.BEGIN.matcher(content);
            if (!mb.find(pos)) break;
            int start = mb.start();
            int afterBegin = mb.end();
            java.util.regex.Matcher me = ToolMarkup.END.matcher(content);
            int endEx;
            String inner;
            if (me.find(afterBegin)) { endEx = me.end(); inner = content.substring(afterBegin, me.start()); }
            else { endEx = content.length(); inner = content.substring(afterBegin); }
            ranges.add(new int[]{start, endEx});
            inners.add(inner);
            pos = endEx;
        }

        boolean hasList = allowedNames != null && !allowedNames.isEmpty();
        JSONArray calls = new JSONArray();
        for (int i = 0; i < inners.size(); i++) {
            JSONArray parsed = filterCalls(parseToolCalls(inners.get(i)), allowedNames);
            // 没有声明工具列表时无法按名字甄别：代码块里的「示例」一律按正文处理
            if (!hasList && inCodeFence(content, ranges.get(i)[0])) parsed = null;
            if (parsed != null) {
                for (int k = 0; k < parsed.length(); k++) calls.put(parsed.opt(k));
            }
        }

        if (calls.length() == 0) {
            // 没有有效调用（模型在正文里描述/举例，或调用块被写坏）：正文语义照常保留，
            // 但**标记残片绝不许上屏** —— 这是修「工具标记漏进正文」的关键一步。
            r.content = ToolMarkup.stripStray(content);
            if (!ranges.isEmpty() || ToolMarkup.earliestSignal(content) >= 0) r.toolError = "NOT_A_TOOL_CALL";
            return r;
        }

        // ② 有有效调用：把所有块从正文里摘掉，块间/块后的正文保留
        String rest = removeRanges(content, ranges);
        r.content = ToolMarkup.stripStray(rest).trim();
        ToolArgsFixer.fix(calls, tools);   // ★ 按声明类型归一化参数（string 参数不得传对象）
        r.toolCalls = calls;
        r.finishReason = "tool_calls";
        return r;
    }

    /** 从文本里移除若干 [start, end) 区间，拼接剩余部分。 */
    private static String removeRanges(String text, java.util.List<int[]> ranges) {
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
     * 列表为空时不做名字校验（部分客户端不传 tools）。
     */
    private static JSONArray filterCalls(JSONArray calls, java.util.Set<String> allowedNames) {
        if (calls == null) return null;
        JSONArray out = new JSONArray();
        for (int i = 0; i < calls.length(); i++) {
            JSONObject c = calls.optJSONObject(i);
            if (c == null) continue;
            String name = c.optString("name", "").trim();
            if (name.isEmpty()) continue;
            if (allowedNames != null && !allowedNames.isEmpty()) {
                boolean hit = false;
                for (String n : allowedNames) {
                    if (n != null && n.trim().equalsIgnoreCase(name)) { hit = true; break; }
                }
                if (!hit) continue;
            }
            out.put(c);
        }
        return out;
    }

    static JSONArray parseToolCalls(String inner) {
        if (inner == null) return null;
        String s = inner.trim();
        // 去掉可能的代码围栏
        s = s.replaceAll("^(?s)```[a-zA-Z0-9]*\\s*", "").replaceAll("(?s)```\\s*$", "").trim();

        // 1) 严格解析：整段 JSON 数组（绝大多数正常输出走这里）
        int lb = s.indexOf('['), rb = s.lastIndexOf(']');
        if (lb >= 0 && rb > lb) {
            JSONArray a = tryArray(s.substring(lb, rb + 1));
            if (a != null) { JSONArray n = normalizeCalls(a); if (n.length() > 0) return n; }
        }
        // 2) 严格解析：整段文本 / 单个对象
        JSONArray a2 = tryArray(s);
        if (a2 != null) { JSONArray n = normalizeCalls(a2); if (n.length() > 0) return n; }
        JSONObject o2 = tryObject(s);
        if (o2 != null) { JSONArray n = wrapCalls(o2); if (n.length() > 0) return n; }

        // 3) 宽松解析：修复「嵌套 / 字符串化 JSON」的多层转义错误（回溯 + 自洽择优）
        //    必须排在 extractObjects 之前：括号切片遇到少转义会产生"语法合法但语义截断"的假对象。
        JSONArray loose = normalizeLoose(LooseJson.parse(s));
        if (loose != null && loose.length() > 0) return loose;

        // 4) 最后的兜底：逐个提取 {...} 片段，再严格 / 宽松各试一次
        for (String ch : extractObjects(s)) {
            JSONObject o = tryObject(ch);
            if (o != null) { JSONArray n = wrapCalls(o); if (n.length() > 0) return n; }
            JSONArray n2 = normalizeLoose(LooseJson.parse(ch));
            if (n2 != null && n2.length() > 0) return n2;
        }
        return null;
    }

    private static JSONArray wrapCalls(JSONObject o) {
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
        if (args instanceof JSONObject || args instanceof JSONArray) { call.put("arguments", LooseJson.normalizeJsonStrings(args)); return; }
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
        List<String> out = new ArrayList<>();
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
