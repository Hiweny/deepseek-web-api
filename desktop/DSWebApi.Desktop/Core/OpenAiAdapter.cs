using System.Text.RegularExpressions;

namespace DSWebApi.Desktop.Core;

/// <summary>OpenAI 兼容协议的请求/响应转换与工具调用解析（与 APK OpenAiAdapter.java 对齐）。</summary>
public static class OpenAiAdapter
{
    public const string START = "<|tool\u2581calls\u2581begin|>";
    public const string END = "<|tool\u2581calls\u2581end|>";
    private const string START_NORM = "<|tool_calls_begin|>";
    private const string END_NORM = "<|tool_calls_end|>";

    public sealed class ChatResult
    {
        public string content = "";
        public string thinking = "";
        public JArr toolCalls = null;
        public string finishReason = "stop";
        /// <summary>工具块存在但解析失败时置位，避免把标记泄漏到正文。</summary>
        public string toolError = "";
    }

    /// <summary>标签归一化（1:1 字符映射，长度不变，索引可对齐）：｜->|，▁->_</summary>
    public static string NormTag(string s)
    {
        if (s == null) return "";
        return s.Replace('\uFF5C', '|').Replace('\u2581', '_');
    }

    public static ChatResult Process(string thinkingText, string contentText) => Process(thinkingText, contentText, null);

    /// <summary>
    /// 从模型输出文本中解析工具调用；返回的 content 为剔除工具块后的正文。
    /// 只有当标记块内确实是「结构合法、且工具名来自本次请求声明的列表」的工具调用时才生效；
    /// 否则一律视为普通正文原样返回（不在正文里吞内容）。
    /// </summary>
    public static ChatResult Process(string thinkingText, string contentText, ICollection<string> allowedNames)
    {
        var r = new ChatResult();
        r.thinking = thinkingText ?? "";
        string content = contentText ?? "";

        string norm = NormTag(content);
        int s = norm.IndexOf(START_NORM, StringComparison.Ordinal);
        if (s < 0) { r.content = content; return r; }
        int e = norm.IndexOf(END_NORM, s + START_NORM.Length, StringComparison.Ordinal);
        string inner;
        int after;
        if (e < 0)
        {
            inner = content.Substring(s + START_NORM.Length);
            after = content.Length;
        }
        else
        {
            inner = content.Substring(s + START_NORM.Length, e - (s + START_NORM.Length));
            after = e + END_NORM.Length;
        }
        JArr calls = FilterCalls(ParseToolCalls(inner), allowedNames);
        // 没有声明工具列表时无法按名字甄别：若这段标记是写在 markdown 代码块里的「示例」，
        // 一律按正文处理（正常调用不会被包在代码块里——提示词规则 5 已明确禁止）。
        bool hasList = allowedNames != null && allowedNames.Count > 0;
        if (!hasList && InCodeFence(content, s)) calls = null;
        if (calls == null || calls.Count == 0)
        {
            // 不是有效工具调用（例如模型只是在正文里描述/举例这个标记）：原样保留整段文本
            r.content = content;
            r.toolError = "NOT_A_TOOL_CALL";
            return r;
        }
        r.toolCalls = calls;
        r.finishReason = "tool_calls";
        string before = content.Substring(0, s).Trim();
        string rest = after < content.Length ? content.Substring(after).Trim() : "";
        r.content = (before + (rest.Length == 0 ? "" : "\n" + rest)).Trim();
        return r;
    }

    /// <summary>只保留「结构完整（有 name）」且「名字确实在本次声明的工具列表里」的调用。</summary>
    private static JArr FilterCalls(JArr calls, ICollection<string> allowedNames)
    {
        if (calls == null) return null;
        bool hasList = allowedNames != null && allowedNames.Count > 0;
        var outp = new JArr();
        for (int i = 0; i < calls.Count; i++)
        {
            var c = calls[i] as JObj;
            if (c == null) continue;
            string name = c.Str("name", "").Trim();
            if (name.Length == 0) continue;
            if (hasList)
            {
                bool hit = false;
                foreach (var n2 in allowedNames)
                {
                    if (n2 != null && n2.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                }
                if (!hit) continue;
            }
            else if (!LooksLikeToolName(name))
            {
                // 无列表时的兜底：占位名（含空白 / 中日韩文字，如「工具名」）不是真实工具名
                continue;
            }
            outp.Add(c);
        }
        return outp;
    }

    public static JArr ParseToolCalls(string inner)
    {
        if (inner == null) return null;
        string s = inner.Trim();
        // 去掉可能的代码围栏
        s = Regex.Replace(s, "^```[a-zA-Z0-9]*\\s*", "");
        s = Regex.Replace(s, "```\\s*$", "").Trim();

        // 1) 严格解析：整段 JSON 数组
        int lb = s.IndexOf('['), rb = s.LastIndexOf(']');
        if (lb >= 0 && rb > lb)
        {
            var a = TryArray(s.Substring(lb, rb + 1 - lb));
            if (a != null) { var nn = NormalizeCalls(a); if (nn.Count > 0) return nn; }
        }
        // 2) 严格解析：整段文本 / 单个对象
        var a2 = TryArray(s);
        if (a2 != null) { var nn = NormalizeCalls(a2); if (nn.Count > 0) return nn; }
        var o2 = TryObject(s);
        if (o2 != null) { var nn = WrapCalls(o2); if (nn.Count > 0) return nn; }

        // 3) 宽松解析：修复「嵌套 / 字符串化 JSON」的多层转义错误（必须排在 extractObjects 之前）
        var loose = NormalizeLoose(LooseJson.Parse(s));
        if (loose != null && loose.Count > 0) return loose;

        // 4) 最后的兜底：逐个提取 {...} 片段
        foreach (string ch in ExtractObjects(s))
        {
            var o = TryObject(ch);
            if (o != null) { var nn = WrapCalls(o); if (nn.Count > 0) return nn; }
            var n2 = NormalizeLoose(LooseJson.Parse(ch));
            if (n2 != null && n2.Count > 0) return n2;
        }
        return null;
    }

    private static JArr WrapCalls(JObj o)
    {
        var a = new JArr();
        a.Add(o);
        return NormalizeCalls(a);
    }

    private static JArr NormalizeLoose(JVal v)
    {
        if (v is JArr ar) return NormalizeCalls(ar);
        if (v is JObj ob) return WrapCalls(ob);
        return null;
    }

    private static JArr TryArray(string s) => Json.TryParse(s) as JArr;
    private static JObj TryObject(string s) => Json.TryParse(s) as JObj;

    /// <summary>统一为 [{name, arguments}]（arguments 为对象，必要时经宽松解析修复）。</summary>
    private static JArr NormalizeCalls(JArr a)
    {
        var outp = new JArr();
        for (int i = 0; i < a.Count; i++)
        {
            var item = a[i] as JObj;
            if (item == null) continue;
            string name = FirstString(item, "name", "tool_name", "tool");
            if (name.Length == 0)
            {
                var fn = item.Obj("function");
                if (fn != null) name = FirstString(fn, "name", "tool_name", "tool");
            }
            if (name.Length == 0) continue;
            JVal args = item.Get("arguments") ?? item.Get("parameters") ?? item.Get("args") ?? item.Get("input");
            if (args == null)
            {
                var fn = item.Obj("function");
                if (fn != null) args = fn.Get("arguments");
            }
            var call = new JObj();
            try
            {
                call.Set("name", name);
                PutArgs(call, args);
            }
            catch (Exception) { }
            outp.Add(call);
        }
        return outp;
    }

    private static string FirstString(JObj o, params string[] keys)
    {
        foreach (string k in keys)
        {
            string v = o.Str(k, "").Trim();
            if (v.Length > 0) return v;
        }
        return "";
    }

    /// <summary>真实工具名不会包含空白或中日韩文字（占位符如「工具名 / 工具」会被排除）。</summary>
    private static bool LooksLikeToolName(string n)
    {
        if (n.Length == 0 || n.Length > 128) return false;
        foreach (char c in n)
        {
            if (char.IsWhiteSpace(c)) return false;
            if ((c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF)
                || (c >= 0xFF00 && c <= 0xFFEF) || (c >= 0x3000 && c <= 0x303F)) return false;
        }
        return true;
    }

    /// <summary>索引处是否位于 markdown 代码围栏（```）内部。</summary>
    private static bool InCodeFence(string content, int idx)
    {
        int count = 0, i = -1;
        while (true)
        {
            i = content.IndexOf("```", i + 1, StringComparison.Ordinal);
            if (i < 0 || i >= idx) break;
            count++;
        }
        return (count % 2) == 1;
    }

    /// <summary>各种形态的 arguments 统一成「对象」；实在解析不出则把原始文本存入 _raw_args。</summary>
    private static void PutArgs(JObj call, JVal args)
    {
        if (args is JObj || args is JArr)
        {
            call.Set("arguments", LooseJson.NormalizeJsonStrings(args));
            return;
        }
        string as_ = args == null ? "" : (args is JStr js ? js.V : args.ToJson()).Trim();
        if (as_.Length == 0 || as_ == "{}") { call.Set("arguments", new JObj()); return; }
        var ao = TryObject(as_);
        if (ao != null) { call.Set("arguments", LooseJson.NormalizeJsonStrings(ao)); return; }
        var lo = LooseJson.Parse(as_);
        if (lo is JObj || lo is JArr) { call.Set("arguments", LooseJson.NormalizeJsonStrings(lo)); return; }
        call.Set("arguments", new JObj());
        call.Set("_raw_args", as_);
    }

    /// <summary>扫描出顶层 {...} 片段（简易括号计数，忽略字符串内括号）。</summary>
    private static List<string> ExtractObjects(string s)
    {
        var outp = new List<string>();
        int depth = 0, start = -1;
        bool inStr = false;
        char q = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == '\\') { i++; continue; }
                if (c == q) inStr = false;
                continue;
            }
            if (c == '"' || c == '\'') { inStr = true; q = c; continue; }
            if (c == '{') { if (depth == 0) start = i; depth++; }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && start >= 0) { outp.Add(s.Substring(start, i + 1 - start)); start = -1; }
            }
        }
        return outp;
    }

    /* ================= 响应构建 ================= */

    public static string NewId() => "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 24);

    public static int EstTokens(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        return Math.Max(1, (int)Math.Ceiling(s.Length / 2.0));
    }

    public static JObj BuildMessage(string thinking, string content, JArr toolCalls)
    {
        var msg = new JObj();
        try
        {
            msg.Set("role", "assistant");
            if (toolCalls != null && toolCalls.Count > 0)
            {
                if (string.IsNullOrEmpty(content)) msg.Set("content", JNull.I);
                else msg.Set("content", content);
                msg.Set("tool_calls", ToOpenAiToolCalls(toolCalls));
            }
            else msg.Set("content", content ?? "");
            if (!string.IsNullOrEmpty(thinking)) msg.Set("reasoning_content", thinking);
        }
        catch (Exception) { }
        return msg;
    }

    public static JArr ToOpenAiToolCalls(JArr calls)
    {
        var outp = new JArr();
        for (int i = 0; i < calls.Count; i++)
        {
            var c = calls[i] as JObj;
            if (c == null) continue;
            string name = c.Str("name", "");
            string argStr = ArgStringOf(c);
            var tc = new JObj();
            var fn = new JObj();
            try
            {
                fn.Set("name", name);
                fn.Set("arguments", argStr);
                tc.Set("id", "call_" + Guid.NewGuid().ToString("N").Substring(0, 22));
                tc.Set("type", "function");
                tc.Set("function", fn);
            }
            catch (Exception) { }
            outp.Add(tc);
        }
        return outp;
    }

    /// <summary>
    /// 生成 OpenAI 要求的 arguments 字符串。无论上游给的是对象还是「字符串化的 JSON」，
    /// 这里都会归一化为**合法 JSON 文本**，彻底消除多层转义导致的解析失败。
    /// </summary>
    private static string ArgStringOf(JObj c)
    {
        JVal args = c.Get("arguments");
        string raw = c.Str("_raw_args", "");
        string text;
        if (args is JObj jo && jo.Count == 0 && raw.Length > 0) text = raw;
        else if (args == null) text = raw;
        else if (args is JStr js) text = js.V;
        else text = args.ToJson();
        string t = (text ?? "").Trim();
        if (t.Length == 0) return "{}";
        // 合法 JSON 直接用严格解析（更快、更稳），只有严格解析失败时才走宽松修复
        var strict = Json.TryParse(t);
        if (strict is JObj || strict is JArr) return strict.ToJson();
        // 过度转义：值本身是「被再转义一层的 JSON 文本」→ 去一层后是对象/数组则采用
        if (strict is JStr st)
        {
            string u = LooseJson.UnescapeOneLevel(st.V).Trim();
            var uv = Json.TryParse(u);
            if (uv is JObj || uv is JArr) return uv.ToJson();
        }
        var parsed = LooseJson.Parse(t);
        if (parsed is JObj || parsed is JArr) return parsed.ToJson();
        return t;
    }

    public static JObj BuildCompletion(string id, string model, long created,
        string thinking, string content, JArr toolCalls, string finishReason,
        int promptTokens, int completionTokens)
    {
        var o = new JObj();
        try
        {
            o.Set("id", id);
            o.Set("object", "chat.completion");
            o.Set("created", created);
            o.Set("model", model);
            var choices = new JArr();
            var ch = new JObj();
            ch.Set("index", 0);
            ch.Set("message", BuildMessage(thinking, content, toolCalls));
            ch.Set("finish_reason", finishReason);
            choices.Add(ch);
            o.Set("choices", choices);
            var usage = new JObj();
            usage.Set("prompt_tokens", (long)promptTokens);
            usage.Set("completion_tokens", (long)completionTokens);
            usage.Set("total_tokens", (long)(promptTokens + completionTokens));
            o.Set("usage", usage);
        }
        catch (Exception) { }
        return o;
    }

    /// <summary>生成一条 SSE chunk（不含 "data: " 前缀）。</summary>
    public static string Chunk(string id, string model, long created, JObj delta, string finishReason)
    {
        var o = new JObj();
        try
        {
            o.Set("id", id);
            o.Set("object", "chat.completion.chunk");
            o.Set("created", created);
            o.Set("model", model);
            var choices = new JArr();
            var ch = new JObj();
            ch.Set("index", 0);
            ch.Set("delta", delta ?? new JObj());
            if (finishReason == null) ch.Set("finish_reason", JNull.I);
            else ch.Set("finish_reason", finishReason);
            choices.Add(ch);
            o.Set("choices", choices);
        }
        catch (Exception) { }
        return o.ToJson();
    }

    public static JObj ModelsList()
    {
        var o = new JObj();
        try
        {
            o.Set("object", "list");
            var data = new JArr();
            data.Add(Model("deepseek"));
            data.Add(Model("deepseek-chat"));
            data.Add(Model("deepseek-reasoner"));
            o.Set("data", data);
        }
        catch (Exception) { }
        return o;
    }

    private static JObj Model(string id)
    {
        var m = new JObj();
        m.Set("id", id);
        m.Set("object", "model");
        m.Set("created", 1700000000L);
        m.Set("owned_by", "deepseek-web");
        return m;
    }
}
