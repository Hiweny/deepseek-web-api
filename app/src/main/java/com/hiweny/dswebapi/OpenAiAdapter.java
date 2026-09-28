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
    }

    /** 标签归一化（1:1 字符映射，长度不变，索引可对齐）：｜->|，▁->_ */
    public static String normTag(String s) {
        if (s == null) return "";
        return s.replace('\uFF5C', '|').replace('\u2581', '_');
    }

    /** 从模型输出文本中解析工具调用；返回的 content 为剔除工具块后的正文。 */
    public static ChatResult process(String thinkingText, String contentText) {
        ChatResult r = new ChatResult();
        r.thinking = thinkingText == null ? "" : thinkingText;
        String content = contentText == null ? "" : contentText;

        String norm = normTag(content);
        int s = norm.indexOf(START_NORM);
        if (s < 0) { r.content = content; return r; }
        int e = norm.indexOf(END_NORM, s + START_NORM.length());
        String inner;
        int after;
        if (e < 0) {
            inner = content.substring(s + START_NORM.length());
            after = content.length();
        } else {
            inner = content.substring(s + START_NORM.length(), e);
            after = e + END_NORM.length();
        }
        JSONArray calls = parseToolCalls(inner);
        if (calls == null || calls.length() == 0) {
            r.content = content;
            return r;
        }
        r.toolCalls = calls;
        r.finishReason = "tool_calls";
        // 工具调用之外若还有正文（少数情况），保留
        String before = content.substring(0, s).trim();
        String rest = after < content.length() ? content.substring(after).trim() : "";
        r.content = (before + (rest.isEmpty() ? "" : "\n" + rest)).trim();
        return r;
    }

    static JSONArray parseToolCalls(String inner) {
        if (inner == null) return null;
        String s = inner.trim();
        // 去掉可能的代码围栏
        s = s.replaceAll("^(?s)```[a-zA-Z0-9]*\\s*", "").replaceAll("(?s)```\\s*$", "").trim();
        int lb = s.indexOf('['), rb = s.lastIndexOf(']');
        if (lb >= 0 && rb > lb) {
            String arr = s.substring(lb, rb + 1);
            JSONArray a = tryArray(arr);
            if (a != null) return normalizeCalls(a);
        }
        // 回退：直接当对象数组，或逐个提取 {...}
        JSONArray a = tryArray(s);
        if (a != null) return normalizeCalls(a);
        List<String> objs = extractObjects(s);
        if (!objs.isEmpty()) {
            JSONArray out = new JSONArray();
            for (String o : objs) {
                JSONObject j = tryObject(o);
                if (j != null) out.put(j);
            }
            if (out.length() > 0) return normalizeCalls(out);
        }
        // 回退：单个对象
        JSONObject one = tryObject(s);
        if (one != null) { JSONArray out = new JSONArray(); out.put(one); return normalizeCalls(out); }
        return null;
    }

    private static JSONArray tryArray(String s) {
        try { return new JSONArray(s); } catch (Exception e) { return null; }
    }
    private static JSONObject tryObject(String s) {
        try { return new JSONObject(s); } catch (Exception e) { return null; }
    }

    /** 统一为 [{name, arguments}]（arguments 为对象）。 */
    private static JSONArray normalizeCalls(JSONArray a) {
        JSONArray out = new JSONArray();
        for (int i = 0; i < a.length(); i++) {
            JSONObject item = a.optJSONObject(i);
            if (item == null) continue;
            String name = item.optString("name", item.optString("function", ""));
            Object args = item.opt("arguments");
            if (args == null && item.has("parameters")) args = item.opt("parameters");
            JSONObject call = new JSONObject();
            try {
                call.put("name", name);
                if (args instanceof JSONObject || args instanceof JSONArray) call.put("arguments", args);
                else if (args instanceof String) {
                    String as = ((String) args).trim();
                    JSONObject ao = tryObject(as);
                    if (ao != null) call.put("arguments", ao);
                    else { call.put("arguments", new JSONObject()); call.put("_raw_args", as); }
                } else call.put("arguments", new JSONObject());
            } catch (Exception ignored) {}
            out.put(call);
        }
        return out;
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
            Object args = c.opt("arguments");
            String argStr = args == null ? "{}" : (args instanceof String ? (String) args : args.toString());
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
