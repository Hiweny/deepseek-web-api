using System.Text;
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
        /// <summary>已被摘除（判定为工具调用 / 写坏的调用块）的区间，供流式收尾补发时裁掉。</summary>
        public List<int[]> droppedSpans;
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
        => Process(thinkingText, contentText, allowedNames, null);

    /// <summary>
    /// 增加 toolSchema：按工具声明的 JSON Schema 归一化参数
    /// （声明为 string 的参数若被写成了对象/数组，会序列化成「只转义一层」的字符串，
    ///   以适配宿主侧「再解析一次 JSON」的约定，见 ToolArgsFixer）。
    /// </summary>
    public static ChatResult Process(string thinkingText, string contentText, ICollection<string> allowedNames,
        Dictionary<string, ToolArgsFixer.Schema> toolSchema)
    {
        var r = new ChatResult();
        r.thinking = thinkingText ?? "";
        string content = contentText ?? "";
        if (content.Length == 0) return r;

        bool hasList = allowedNames != null && allowedNames.Count > 0;

        // ① 候选块 A：**带标记**的工具块（宽容正则，覆盖竖线半角/全角、分隔符 _/▁/空格、大小写等变体，
        //    以及只有 begin 没有 end 的截断块）。旧实现用 NormTag + 精确 IndexOf，只认一种形态。
        var blocks = new List<int[]>();
        var inners = new List<string>();
        int pos = 0;
        while (pos < content.Length)
        {
            var mb = ToolMarkup.BeginRe.Match(content, pos);
            if (!mb.Success) break;
            int start = mb.Index;
            int afterBegin = mb.Index + mb.Length;
            var me = ToolMarkup.EndRe.Match(content, afterBegin);
            int endEx; string inner;
            if (me.Success) { endEx = me.Index + me.Length; inner = content.Substring(afterBegin, me.Index - afterBegin); }
            else { endEx = content.Length; inner = content.Substring(afterBegin); }
            blocks.Add(new[] { start, endEx });
            inners.Add(inner);
            pos = endEx;
        }

        // ② 候选块 B：**没有标记的裸 JSON**（模型漏写标记时的兜底）。
        //    只有客户端确实声明了工具时才启用 —— 否则会误伤正文里正常输出的 JSON。
        if (hasList && blocks.Count == 0)
        {
            foreach (var sp in FindBareJsonSpans(content))
            {
                blocks.Add(sp);
                inners.Add(content.Substring(sp[0], sp[1] - sp[0]));
            }
        }

        var calls = new JArr();
        var drop = new List<int[]>();       // 必须从正文里摘掉的区间
        bool badCall = false;               // 存在「像调用但解析不出来」的块
        for (int i = 0; i < blocks.Count; i++)
        {
            string inner = inners[i];
            bool inFence = InCodeFence(content, blocks[i][0]);
            bool looksCall = ToolMarkup.LooksLikeToolCallJson(inner);
            var raw = ParseToolCalls(inner);
            var parsed = FilterCalls(raw, allowedNames);
            // 没有声明工具列表时无法按名字甄别：代码块里的「示例」一律按正文处理
            // （正常调用不会被包在代码块里——提示词规则 5 已明确禁止）。
            bool accepted = parsed != null && parsed.Count > 0 && !(!hasList && inFence);
            if (accepted)
            {
                for (int k = 0; k < parsed.Count; k++) calls.Add(parsed[k]);
                drop.Add(blocks[i]);
            }
            else if (looksCall)
            {
                bool parseFailed = raw == null || raw.Count == 0;
                bool realNameNotAllowed = !parseFailed && hasList && HasRealLookingName(raw);
                if (parseFailed || realNameNotAllowed)
                {
                    // ★ 关键修复：块「像工具调用」却用不了（JSON 写坏 / 被截断 / 名字写错）
                    //   → 整块摘除。旧实现把坏 JSON 原样留在正文里，这正是「工具调用漏进正文」的根因。
                    drop.Add(blocks[i]);
                    badCall = true;
                    Log.Write("工具调用块不可用，已丢弃以免泄漏: " + Brief(inner));
                }
                // 否则：正文里举例说明（名字是占位符，如「工具名」）→ 原样保留
            }
            // 否则：正文里对标记的讨论 → 原样保留
        }

        if (calls.Count == 0)
        {
            r.content = ToolMarkup.StripStray(ToolMarkup.RemoveSpans(content, drop));
            r.droppedSpans = drop;
            if (badCall)
            {
                r.toolError = "TOOL_PARSE_FAILED";
                // 静默空回复最危险（用户/宿主以为"正常结束"）：给一句明确提示，原文只进日志。
                if (r.content.Trim().Length == 0) r.content = "[工具调用格式异常，已丢弃以免泄漏到正文，请重试]";
            }
            else if (blocks.Count > 0 || ToolMarkup.EarliestSignal(content) >= 0) r.toolError = "NOT_A_TOOL_CALL";
            return r;
        }

        // ③ 有有效调用：把所有块从正文里摘掉，块间/块后的正文保留
        // 既然这一轮确实有调用，所有候选块（含写坏的那个）一律摘掉，避免任何残片漏网
        r.content = ToolMarkup.StripStray(ToolMarkup.RemoveSpans(content, blocks)).Trim();
        r.droppedSpans = blocks;
        ToolArgsFixer.Fix(calls, toolSchema);   // ★ 按声明类型归一化参数（string 参数不得传对象）
        r.toolCalls = calls;
        r.finishReason = "tool_calls";
        return r;
    }

    /// <summary>解析结果里是否存在「像真工具名」的名字（区分写错名的调用与正文占位符举例）。</summary>
    private static bool HasRealLookingName(JArr raw)
    {
        if (raw == null) return false;
        foreach (var it in raw.Items)
        {
            var o = it as JObj;
            if (o != null && LooksLikeToolName(o.Str("name", "").Trim())) return true;
        }
        return false;
    }

    private static string Brief(string s)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 160 ? s : s.Substring(0, 160) + "...";
    }

    /// <summary>定位「没有标记的裸 JSON 工具调用块」的区间（配平扫描；截断则吃到文末）。</summary>
    private static List<int[]> FindBareJsonSpans(string content)
    {
        var outp = new List<int[]>();
        foreach (Match m in ToolMarkup.BareJsonSigRe.Matches(content))
        {
            int start = m.Index;
            if (outp.Count > 0 && start < outp[outp.Count - 1][1]) continue;   // 与上一块重叠
            int end = JsonRepair.BalancedEnd(content, start);
            if (end < 0) end = content.Length;                                 // 截断 → 吃到文末
            outp.Add(new[] { start, end });
            if (outp.Count >= 4) break;
        }
        return outp;
    }

    /// <summary>把「绝对区间」裁到 tail（起始于 offset）内并移除 —— 流式收尾补发时用。</summary>
    public static string CutSpans(string tail, int offset, List<int[]> spans)
    {
        if (string.IsNullOrEmpty(tail) || spans == null || spans.Count == 0) return tail;
        var rel = new List<int[]>();
        foreach (var sp in spans)
        {
            int s0 = Math.Max(sp[0], offset) - offset;
            int e0 = Math.Min(sp[1], offset + tail.Length) - offset;
            if (e0 > s0) rel.Add(new[] { s0, e0 });
        }
        return ToolMarkup.RemoveSpans(tail, rel);
    }

    /// <summary>从文本里移除若干 [start, end) 区间，拼接剩余部分。</summary>
    private static string RemoveRanges(string text, List<int[]> ranges)
    {
        if (ranges.Count == 0) return text;
        var sb = new StringBuilder();
        int cur = 0;
        foreach (var rg in ranges)
        {
            int s0 = rg[0], e0 = rg[1];
            if (s0 > cur) sb.Append(text, cur, s0 - cur);
            if (e0 > cur) cur = e0;
        }
        if (cur < text.Length) sb.Append(text, cur, text.Length - cur);
        return sb.ToString();
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
        if (s.Length == 0) return null;

        // 多候选：原文 → 结构补括号 → 全角归一 → 全角归一+补括号。
        // 顺序有讲究：不改动内容的候选优先，改动越大的越靠后。
        var cands = new List<string> { s };
        var rb = JsonRepair.TryRebuild(s);
        if (rb != null && rb != s) cands.Add(rb);
        var fw = JsonRepair.NormalizeFullWidth(s);
        if (fw != s)
        {
            cands.Add(fw);
            var rb2 = JsonRepair.TryRebuild(fw);
            if (rb2 != null && rb2 != fw) cands.Add(rb2);
        }
        foreach (string cand in cands)
        {
            var res = ParseCore(cand);
            // ⚠️ 必须校验「名字健全」：宽松解析器对**未修复的原文**也能吐出一个
            //   乱码调用名（如 `pwsh\",\"arguments\":{...`），若就此返回，就会短路掉
            //   后面真正能修好的候选（实测：全角冒号样本因此救不回来）。
            if (res != null && res.Count > 0 && NamesSane(res)) return res;
        }
        return null;
    }

    /// <summary>调用名是否「像真的工具名」（拦掉宽松解析产生的乱码名）。</summary>
    private static bool NamesSane(JArr calls)
    {
        if (calls == null) return false;
        foreach (var it in calls.Items)
        {
            var o = it as JObj;
            if (o == null) return false;
            string n = o.Str("name", "").Trim();
            if (n.Length == 0 || n.Length > 128) return false;
            foreach (char c in n)
            {
                if (char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '\\' || c == '{' || c == '}'
                    || c == '[' || c == ']' || c == ',' || c == '：' || c == '，') return false;
            }
        }
        return true;
    }

    /// <summary>对**单一候选文本**多级解析（严格 → 宽松 → 逐对象提取）。</summary>
    private static JArr ParseCore(string s)
    {
        // 1) 严格解析：整段 JSON 数组
        int lb = s.IndexOf('['), rb = s.LastIndexOf(']');
        if (lb >= 0 && rb > lb)
        {
            var a = TryArray(s.Substring(lb, rb + 1 - lb));
            if (a != null) { var nn = NormalizeCalls(a); if (nn.Count > 0) return nn; }
        }
        // 2) 严格解析：整段文本 / 单个对象（含 {"tool_calls":[…]} 包装）
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
        // {"tool_calls":[…]} / {"function_calls":[…]} 这类**包装层**：直接取内层数组
        foreach (string k in new[] { "tool_calls", "tool_call", "function_calls", "calls" })
        {
            var v = o.Get(k);
            if (v is JArr arr)
            {
                var nn0 = NormalizeCalls(arr);
                if (nn0.Count > 0) return nn0;
            }
        }
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
        // 模型把 arguments 套成了一层数组 [{…}]（实测：arguments 写成数组后 ]/} 顺序错乱）→ 取唯一元素
        if (args is JArr arr1 && arr1.Count == 1 && arr1[0] is JObj) args = arr1[0];
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
