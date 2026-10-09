using System.Runtime.InteropServices;
using System.Text;
using DSWebApi.Desktop.Core;

namespace DSWebApi.Desktop;

/// <summary>
/// 无界面自检（--selftest）：验证工具调用解析 / 转义对齐 / 正文不吞内容。
/// 与 APK v1.0.5 的端到端用例对齐，用于 CI 真机与本地验证。
/// </summary>
internal static class SelfTest
{
    private static int _pass, _fail;
    private static readonly List<string> _lines = new List<string>();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void Say(string line)
    {
        _lines.Add(line);
        try { Console.WriteLine(line); } catch { }
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Say("  PASS  " + name); }
        else { _fail++; Say("  FAIL  " + name + (detail.Length > 0 ? "  << " + detail : "")); }
    }

    /// <summary>下游校验：arguments -> 各字符串值（若像 JSON）-> 再深一层，全部必须严格合法。</summary>
    private static bool DeepParseOk(string argsJson)
    {
        var v = Json.TryParse(argsJson);
        return v != null && DeepWalk(v, 0);
    }

    private static bool DeepWalk(JVal v, int depth)
    {
        if (depth > 4) return true;
        if (v is JObj o)
        {
            foreach (var k in o.Keys.ToList())
            {
                if (!DeepOne(o.Get(k), depth)) return false;
            }
            return true;
        }
        if (v is JArr a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (!DeepOne(a[i], depth)) return false;
            }
            return true;
        }
        return true;
    }

    private static bool DeepOne(JVal c, int depth)
    {
        if (c == null) return true;
        if (c is JStr s)
        {
            string t = s.V.Trim();
            if (t.Length >= 2 && (t[0] == '{' || t[0] == '['))
            {
                var inner = Json.TryParse(t);
                if (inner == null) return false;
                return DeepWalk(inner, depth + 1);
            }
            return true;
        }
        return DeepWalk(c, depth + 1);
    }

    /// <summary>完整链路：模型文本 -> OpenAiAdapter -> OpenAI tool_calls -> 逐层严格解析。</summary>
    private static bool ChainOk(string innerJson, HashSet<string> tools, out string err)
    {
        err = "";
        string content = OpenAiAdapter.START + innerJson + OpenAiAdapter.END;
        var cr = OpenAiAdapter.Process("", content, tools);
        if (cr.toolCalls == null || cr.toolCalls.Count == 0)
        {
            err = "未识别为工具调用 toolError=" + cr.toolError;
            return false;
        }
        var oai = OpenAiAdapter.ToOpenAiToolCalls(cr.toolCalls);
        var fn = (oai[0] as JObj)?.Obj("function");
        string args = fn?.Str("arguments") ?? "";
        if (!DeepParseOk(args))
        {
            err = "arguments 无法逐层严格解析: " + args;
            return false;
        }
        return true;
    }

    /// <summary>构造 OpenAI tools 数组（参数 schema 用 JSON 文本给出）。</summary>
    private static JArr ToolsOf(params (string name, string schema)[] items)
    {
        var arr = new JArr();
        foreach (var it in items)
        {
            var t = new JObj();
            var fn = new JObj();
            fn.Set("name", it.name);
            fn.Set("parameters", it.schema);
            t.Set("function", fn);
            arr.Add(t);
        }
        return arr;
    }

    /// <summary>走完整链路（带 schema）并返回第一个调用的 arguments 对象。</summary>
    private static JObj RunFix(string innerJson, JArr tools, out string err)
    {
        err = "";
        var index = ToolArgsFixer.BuildIndex(tools);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in tools.Items)
        {
            var t = it as JObj;
            if (t == null) continue;
            var fn = t.Obj("function") ?? t;
            string n = fn.Str("name", "").Trim();
            if (n.Length > 0) names.Add(n);
        }
        string content = OpenAiAdapter.START + innerJson + OpenAiAdapter.END;
        var cr = OpenAiAdapter.Process("", content, names, index);
        if (cr.toolCalls == null || cr.toolCalls.Count == 0) { err = "未识别为工具调用 toolError=" + cr.toolError; return null; }
        var oai = OpenAiAdapter.ToOpenAiToolCalls(cr.toolCalls);
        var fn2 = (oai[0] as JObj)?.Obj("function");
        string argStr = fn2?.Str("arguments") ?? "";
        var args = Json.TryParse(argStr) as JObj;
        if (args == null) { err = "arguments 不是合法 JSON 对象: " + argStr; return null; }
        return args;
    }

    /// <summary>宿主视角：声明为 string 的字段必须是字符串，且该字符串若像 JSON 必须严格合法。</summary>
    private static bool HostStrOk(JObj args, string field, out string err)
    {
        err = "";
        var v = args.Get(field);
        if (!(v is JStr js)) { err = field + " 不是字符串，实际类型=" + (v == null ? "null" : v.GetType().Name); return false; }
        string t = js.V.Trim();
        if (t.Length >= 2 && (t[0] == '{' || t[0] == '['))
        {
            var inner = Json.TryParse(t);
            if (inner == null) { err = field + " 的字符串内容不是严格合法 JSON: " + t; return false; }
            if (!DeepWalk(inner, 0)) { err = field + " 内层还有非法 JSON: " + t; return false; }
        }
        return true;
    }

    private static string MarkerCall(string name, string argsJson)
    {
        return OpenAiAdapter.START + "[{\"name\":\"" + name + "\",\"arguments\":" + argsJson + "}]" + OpenAiAdapter.END;
    }

    private static string Q(string s) => Json.Quote(s);

    public static int Run()
    {
        try { AttachConsole(-1); } catch { }
        Say("=== DeepSeek Web API 桌面端自检 ===");

        var tools = new HashSet<string>(
            new[] { "read_file", "http_request", "multipart_request", "terminal", "create_memory" },
            StringComparer.OrdinalIgnoreCase);

        // 内层数组的「真值」（模型常把它当字符串塞进 files）
        const string FilesRaw = "[{\"field_name\":\"files[]\",\"file_path\":\"/a.txt\"}]";

        // A) 规范输出：嵌套 JSON 已正确转义
        string a = MarkerCall("http_request",
            "{\"params\":{\"url\":\"http://a\",\"headers\":" + Q("{\"X-Test\":\"hello\"}") + "}}");
        Check("A 规范转义 + 嵌套对象", ChainOk(a, tools, out var e1), e1);

        // B) 关键场景：arguments 是字符串化 JSON，且内层 files 的引号「少转义」
        string argsB = "{\"params\":{\"files\":\"" + FilesRaw + "\"}}";
        string b = "[{\"name\":\"multipart_request\",\"arguments\":" + Q(argsB) + "}]";
        Check("B arguments 字符串化 + 内层少转义", ChainOk(b, tools, out var e2), e2);

        // C) 嵌套对象里 files 的引号完全裸露
        string c = MarkerCall("multipart_request",
            "{\"params\":{\"files\":\"" + FilesRaw + "\"}}");
        Check("C 内层引号全裸", ChainOk(c, tools, out var e3), e3);

        // D) arguments 整体字符串化（转义正确）
        string d = "[{\"name\":\"read_file\",\"arguments\":" + Q("{\"path\":\"/tmp/a.txt\"}") + "}]";
        Check("D arguments 整体字符串化", ChainOk(d, tools, out var e4), e4);

        // E) headers 正确转义
        string e = MarkerCall("http_request",
            "{\"params\":{\"headers\":" + Q("{\"A\":\"1\"}") + "}}");
        Check("E headers 正确转义", ChainOk(e, tools, out var e5), e5);

        // F) 扁平纯文本参数回归
        string f = MarkerCall("terminal", "{\"command\":\"ls -la\"}");
        Check("F 扁平纯文本参数回归", ChainOk(f, tools, out var e6), e6);

        // G) 缺结束标记
        {
            string content = OpenAiAdapter.START
                + "[{\"name\":\"read_file\",\"arguments\":{\"path\":\"/x\"}}]";
            var cr = OpenAiAdapter.Process("", content, tools);
            Check("G 缺结束标记仍可识别", cr.toolCalls != null && cr.toolCalls.Count == 1,
                "tc=" + (cr.toolCalls?.Count ?? -1) + " err=" + cr.toolError);
        }

        // H) 正文里解释标记（假工具名）→ 不得当成工具调用，正文必须完整保留
        {
            string body = "工具调用长这样："
                + OpenAiAdapter.START + "[{\"name\": \"工具名\", \"arguments\": {}}]" + OpenAiAdapter.END
                + "，你懂了吗？";
            var cr = OpenAiAdapter.Process("", body, tools);
            // 不误判（toolCalls==null）+ 正文文字保留 + 标记残片剥离（不许上屏）
            Check("H 正文解释标记不误判且不吞内容",
                cr.toolCalls == null && cr.toolError == "NOT_A_TOOL_CALL"
                && cr.content.Contains("你懂了吗") && cr.content.IndexOf("tool\u2581calls", StringComparison.Ordinal) < 0,
                "tc=" + (cr.toolCalls?.Count) + " content='" + cr.content + "'");
        }

        // I) 代码块内的标记
        {
            string body = "示例：\n```\n"
                + OpenAiAdapter.START + "[{\"name\":\"xxx\",\"arguments\":{}}]" + OpenAiAdapter.END
                + "\n```\n以上。";
            var cr = OpenAiAdapter.Process("", body, tools);
            Check("I 代码块内标记不误判且不吞内容",
                cr.toolCalls == null && cr.content.Contains("以上。")
                && cr.content.IndexOf("tool\u2581calls", StringComparison.Ordinal) < 0,
                "tc=" + (cr.toolCalls?.Count) + " content='" + cr.content + "'");
        }

        // J) 标记内 JSON 非法 → 原样返回全文
        {
            string body = OpenAiAdapter.START + "[not a json]" + OpenAiAdapter.END + "尾部说明";
            var cr = OpenAiAdapter.Process("", body, tools);
            Check("J 非法内容不误判且保留正文", cr.toolCalls == null && cr.content.Contains("尾部说明")
                && cr.content.IndexOf("tool\u2581calls", StringComparison.Ordinal) < 0,
                "tc=" + (cr.toolCalls?.Count) + " content='" + cr.content + "'");
        }

        // K) 真实调用：正文只有标记 → 正文清空、finish_reason=tool_calls
        {
            string content = MarkerCall("read_file", "{\"path\":\"/a\"}");
            var cr = OpenAiAdapter.Process("", content, tools);
            Check("K 真实调用：正文清空 + finish_reason",
                cr.toolCalls != null && cr.content.Length == 0 && cr.finishReason == "tool_calls",
                "content='" + cr.content + "' fr=" + cr.finishReason);
        }

        // L) 未声明工具名时不校验（兼容不传 tools 的客户端）
        {
            var cr = OpenAiAdapter.Process("", MarkerCall("any_tool", "{}"), null);
            Check("L 无 tools 列表时不校验名字", cr.toolCalls != null && cr.toolCalls.Count == 1,
                "tc=" + (cr.toolCalls?.Count));
        }

        // M) 序列化往返（中文 / 引号 / 反斜杠 / TAB）
        {
            string tricky = "中文 \"引号\" " + "\\" + "反斜杠" + "\t" + "tab";
            var o = new JObj();
            o.Set("text", tricky);
            o.Set("n", 123L);
            o.Set("b", true);
            var arr = new JArr();
            arr.Add(new JStr("x"));
            o.Set("a", arr);
            string json = o.ToJson();
            var back = Json.TryParse(json) as JObj;
            Check("M 序列化往返（中文/引号/转义）",
                back != null && back.Str("text") == tricky && back.Long("n") == 123, json);
        }

        // N) PromptBuilder：工具提示词包含规则 8/9/10
        {
            var req = new JObj();
            var msgs = new JArr();
            var m = new JObj();
            m.Set("role", "user");
            m.Set("content", "hi");
            msgs.Add(m);
            req.Set("messages", msgs);
            var toolArr = new JArr();
            var t = new JObj();
            var fn = new JObj();
            fn.Set("name", "read_file");
            fn.Set("parameters", new JObj());
            t.Set("function", fn);
            toolArr.Add(t);
            req.Set("tools", toolArr);
            var pb = PromptBuilder.Build(req, true);
            bool ok = pb.toolNames.Count == 1
                      && pb.text.Contains("必须是一个 **JSON 对象**")
                      && pb.text.Contains("转义**一层**")
                      && pb.text.Contains("严格按照参数声明的类型写值")
                      && pb.text.Contains("严禁在正文");
            Check("N 提示词规则 8/9/10 就位", ok, "len=" + pb.text.Length);
        }

        // O) 纯函数
        Check("O EstTokens / NormTag",
            OpenAiAdapter.EstTokens("abcd") == 2
            && OpenAiAdapter.NormTag("<|tool\u2581calls\u2581begin|>") == "<|tool_calls_begin|>");

        // Q) 无 tools 列表时：中文占位名不当成工具调用
        {
            string body = "工具调用长这样："
                + OpenAiAdapter.START + "[{\"name\": \"工具名\", \"arguments\": {}}]" + OpenAiAdapter.END
                + " 完。";
            var cr = OpenAiAdapter.Process("", body, null);
            Check("Q 无列表：中文占位名不误判",
                cr.toolCalls == null && cr.content.Contains("完。")
                && cr.content.IndexOf("tool\u2581calls", StringComparison.Ordinal) < 0, "tc=" + (cr.toolCalls?.Count));
        }

        // R) 无 tools 列表时：代码块内的标记不当成工具调用
        {
            string body = "示例：\n```\n"
                + OpenAiAdapter.START + "[{\"name\":\"foo\",\"arguments\":{}}]" + OpenAiAdapter.END
                + "\n```\n以上。";
            var cr = OpenAiAdapter.Process("", body, null);
            Check("R 无列表：代码块内标记不误判",
                cr.toolCalls == null && cr.content.Contains("以上。")
                && cr.content.IndexOf("tool\u2581calls", StringComparison.Ordinal) < 0, "tc=" + (cr.toolCalls?.Count));
        }

        // P) 过度转义（多一层）也能还原成对象，而不是丢给下游一个字符串
        {
            string doubly = Q(Q("{\"path\":\"/a\"}"));
            string p = "[{\"name\":\"read_file\",\"arguments\":" + doubly + "}]";
            Check("P 过度转义 arguments 仍还原为对象", ChainOk(p, tools, out var e7), e7);
        }

        // ★★★ 工具调用参数「按声明类型」归一化（宿主会对 string 参数再解析一次 JSON）★★★

        // T) 关键修复：schema 声明 params 为 string，模型却输出了对象 → 必须归一化成字符串
        {
            var tl = ToolsOf(("package_proxy",
                "{\"type\":\"object\",\"properties\":{\"tool_name\":{\"type\":\"string\"},\"params\":{\"type\":\"string\"}},\"required\":[\"tool_name\",\"params\"]}"));
            string inner = "{\"tool_name\":\"browser:click\",\"params\":{\"function\":\"() => 1\"}}";
            var args = RunFix(MarkerCall("package_proxy", inner), tl, out var eT);
            bool ok = args != null && HostStrOk(args, "params", out eT) && HostStrOk(args, "tool_name", out eT)
                      && args.Str("params").Contains("function");
            Check("T 声明为 string 的参数被写成对象 → 归一化为字符串", ok, eT);
        }

        // U) 反向保护：schema 声明 params 为 object → 必须保持对象，绝不能被改成字符串
        {
            var tl = ToolsOf(("pkg",
                "{\"type\":\"object\",\"properties\":{\"params\":{\"type\":\"object\"}}}"));
            var args = RunFix(MarkerCall("pkg", "{\"params\":{\"a\":1}}"), tl, out var eU);
            bool ok = args != null && args.Get("params") is JObj;
            Check("U 声明为 object 的参数保持对象（不反向改写）", ok, eU + " 实际=" + (args?.Get("params")?.GetType().Name ?? "null"));
        }

        // V) 幂等：模型已经写成正确的一层转义字符串 → 内容原样保留
        {
            var tl = ToolsOf(("pkg", "{\"type\":\"object\",\"properties\":{\"params\":{\"type\":\"string\"}}}"));
            string want = "{\"a\":1}";
            var args = RunFix(MarkerCall("pkg", "{\"params\":" + Q(want) + "}"), tl, out var eV);
            bool ok = args != null && HostStrOk(args, "params", out eV) && args.Str("params") == want;
            Check("V 已正确的字符串参数保持原样（幂等）", ok, eV + " got=" + (args?.Str("params") ?? "null"));
        }

        // W) 声明为 string 且被多重转义 → 还原成严格合法的一层
        {
            var tl = ToolsOf(("pkg", "{\"type\":\"object\",\"properties\":{\"params\":{\"type\":\"string\"}}}"));
            string doubly = "{\\\"a\\\":1}";                 // 内容里带着字面反斜杠（多转义了一层）
            var args = RunFix(MarkerCall("pkg", "{\"params\":" + Q(doubly) + "}"), tl, out var eW);
            bool ok = args != null && HostStrOk(args, "params", out eW)
                      && (Json.TryParse(args.Str("params")) as JObj)?.Long("a") == 1;
            Check("W 声明为 string 且被多重转义 → 还原为一层", ok, eW);
        }

        // X) 没有类型声明时一律不改写（保守，保留旧行为）
        {
            var tl = ToolsOf(("pkg", "{}"));
            var args = RunFix(MarkerCall("pkg", "{\"params\":{\"a\":1}}"), tl, out var eX);
            bool ok = args != null && args.Get("params") is JObj;
            Check("X 无类型声明时不改写（保守）", ok, eX + " 实际=" + (args?.Get("params")?.GetType().Name ?? "null"));
        }

        // Y) 端到端形态：package_proxy 的 params 内含 JSON 文本，宿主可再解析
        {
            var tl = ToolsOf(("package_proxy",
                "{\"type\":\"object\",\"properties\":{\"tool_name\":{\"type\":\"string\"},\"params\":{\"type\":\"string\"}},\"required\":[\"tool_name\",\"params\"]}"));
            string inner = "{\"tool_name\":\"browser:navigate\",\"params\":{\"url\":\"https://a.com\",\"opts\":{\"wait\":1}}}";
            var args = RunFix(MarkerCall("package_proxy", inner), tl, out var eY);
            var parsed = args == null ? null : Json.TryParse(args.Str("params")) as JObj;
            bool ok = args != null && HostStrOk(args, "params", out eY)
                      && parsed != null && parsed.Str("url") == "https://a.com"
                      && (parsed.Obj("opts")?.Long("wait") ?? -1) == 1;
            Check("Y 端到端：对象参数归一化后宿主可再解析", ok, eY);
        }

        // S) 关键回归：bridge.js 内嵌资源必须能载入（曾因逻辑名不匹配导致 0 字节、API 全部 NO_BRIDGE）
        {
            Log.Init();
            string js = Program.LoadEmbedded("bridge.js");
            Check("S bridge.js 内嵌资源可载入且非空",
                js.Length > 5000 && js.Contains("window.DSKB") && js.Contains("__DSWB_LOADED"),
                "bytes=" + js.Length);
        }

        // Z1~Z6) 工具标记残片绝不许上屏（旧版会漏进正文 —— 用户实测的 bug）
        {
            var cr = OpenAiAdapter.Process("", "正文。\n</|tool\u2581calls\u2581end|>", tools);
            Check("Z1 孤立 end 残片剥离",
                cr.content.IndexOf("tool\u2581calls", StringComparison.Ordinal) < 0 && cr.content.Contains("正文。"),
                "content='" + cr.content + "'");
        }
        {
            var cr = OpenAiAdapter.Process("", "正文。\nvoke>\n</ calls>", tools);
            Check("Z2 只剩后半截残片剥离",
                cr.content.IndexOf("voke>", StringComparison.Ordinal) < 0
                && cr.content.IndexOf("calls>", StringComparison.Ordinal) < 0 && cr.content.Contains("正文。"),
                "content='" + cr.content + "'");
        }
        {
            var cr = OpenAiAdapter.Process("", "正文。<| tool_calls_begin |>xx", tools);
            Check("Z3 变体 begin（空格）剥离",
                cr.content.IndexOf("tool_calls_begin", StringComparison.Ordinal) < 0 && cr.content.Contains("正文。"),
                "content='" + cr.content + "'");
        }
        {
            var cr = OpenAiAdapter.Process("", "正文。<\uFF5Ctool\u2581calls\u2581begin\uFF5C>xx", tools);
            Check("Z4 全角竖线变体剥离",
                cr.content.IndexOf("\uFF5Ctool", StringComparison.Ordinal) < 0 && cr.content.Contains("正文。"),
                "content='" + cr.content + "'");
        }
        {
            // 流式安全边界：正常正文里行内讨论 <invoke> 不该被当成标记信号
            string plain = "在 HTML 里 <invoke> 不是标准标签。";
            Check("Z5 SafeEmitEnd 不误伤行内讨论", ToolMarkup.EarliestSignal(plain) < 0,
                "signal=" + ToolMarkup.EarliestSignal(plain));
        }
        {
            // 流式安全边界：标记出现处之前才可上屏
            int cut = ToolMarkup.SafeEmitEnd("正文。" + OpenAiAdapter.START + "[A]" + OpenAiAdapter.END);
            Check("Z6 流式安全边界停在标记之前", cut == 3, "cut=" + cut);
        }

        {
            // Z7 变体矩阵：竖线（半角/全角/带空格）× 分隔符（_/▁/空格）× 大小写 任意组合
            string[] bars = { "|", "\uFF5C", "| " };
            string[] seps = { "_", "\u2581", " " };
            string[] lows = { "tool", "TOOL" };
            string[] ends = { "end", "END" };
            int bad = 0, total = 0;
            foreach (var bar in bars)
                foreach (var sep in seps)
                    foreach (var lo in lows)
                        foreach (var en in ends)
                        {
                            total++;
                            string b0 = "<" + bar + lo + sep + "calls" + sep + "begin" + bar + ">";
                            string e0 = "<" + bar + lo + sep + "calls" + sep + en + bar + ">";
                            string body = "前置。" + b0 + "[{\"name\":\"read_file\",\"arguments\":{}}]" + e0 + "后置。";
                            var cr = OpenAiAdapter.Process("", body, tools);
                            if (cr.toolCalls == null || cr.toolCalls.Count != 1) { bad++; continue; }
                            if (cr.content.IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0
                                && cr.content.IndexOf("calls", StringComparison.OrdinalIgnoreCase) >= 0) bad++;
                        }
            Check("Z7 变体矩阵全部识别且不残留 (" + total + " 种)", bad == 0, "bad=" + bad + "/" + total);
        }
        {
            // Z8 流式逐字符（最细粒度）：累计上屏正文 + 收尾，都不许含标记；识别为调用时不补发 JSON
            string full = "完成安装说明。\n\n\n\n" + OpenAiAdapter.START
                + "[{\"name\":\"read_file\",\"arguments\":{}}]" + OpenAiAdapter.END;
            int emittedLen = 0; bool toolMode = false;
            for (int i = 1; i <= full.Length; i++)
            {
                string ct = full.Substring(0, i);
                if (toolMode) continue;
                int emit2 = ToolMarkup.SafeEmitEnd(ct);
                if (emit2 > emittedLen) emittedLen = emit2;
                if (ToolMarkup.EarliestSignal(ct) >= 0) toolMode = true;
            }
            var cr2 = OpenAiAdapter.Process("", full, tools);
            string all = full.Substring(0, emittedLen);
            if (cr2.toolCalls == null) all += ToolMarkup.StripStray(full.Substring(emittedLen));
            Check("Z8 流式逐字符不泄漏",
                all.IndexOf("tool", StringComparison.OrdinalIgnoreCase) < 0
                && all.IndexOf("calls", StringComparison.OrdinalIgnoreCase) < 0 && all.Contains("完成安装说明"),
                "all='" + all + "'");
        }
        {
            // Z9 流式：模型把开始标签写丢，只剩孤立闭合标签 —— 残片绝不许上屏
            string full = "正文一。\n" + "</|tool\u2581calls\u2581end|>";
            int emittedLen = 0;
            for (int i = 1; i <= full.Length; i++)
            {
                int emit2 = ToolMarkup.SafeEmitEnd(full.Substring(0, i));
                if (emit2 > emittedLen) emittedLen = emit2;
            }
            string all = full.Substring(0, emittedLen) + ToolMarkup.StripStray(full.Substring(emittedLen));
            Check("Z9 流式孤立残片不泄漏",
                all.IndexOf("tool", StringComparison.OrdinalIgnoreCase) < 0 && all.Contains("正文一"),
                "all='" + all + "'");
        }
        {
            // Z10 随机 fuzz：对标记做随机字符扰动，只要仍被识别为调用，正文就不许残留标记
            var rnd = new Random(20261008);
            // 只对「标记的分隔符位置」随机化（半角/全角竖线、_ / ▁ / 空格 / 空、多写竖线）——
            // 这才是模型真实的「写残」分布；不往标记里乱插 <、> 等结构符（那已不构成标记）。
            string[] bars = { "|", "\uFF5C", "", "| ", "||" };
            string[] seps = { "_", "\u2581", " ", "", "|", "\uFF5C", "__" };
            int leak = 0, miss = 0; string leakSample = "";
            string MkTag(string w)
            {
                string bx = bars[rnd.Next(bars.Length)];
                return "<" + bx + "tool" + seps[rnd.Next(seps.Length)] + "calls"
                    + seps[rnd.Next(seps.Length)] + w + seps[rnd.Next(seps.Length)] + bx + ">";
            }
            for (int it = 0; it < 500; it++)
            {
                string body = "前。" + MkTag("begin") + "[{\"name\":\"read_file\",\"arguments\":{}}]" + MkTag("end") + "后。";
                var cr3 = OpenAiAdapter.Process("", body, tools);
                if (cr3.toolCalls == null || cr3.toolCalls.Count == 0)
                {
                    miss++;
                    if (miss <= 3) leakSample += "\n   MISS=[" + body + "] -> [" + cr3.content + "]";
                    continue;
                }
                string rest = cr3.content;
                bool dirty = ToolMarkup.StrongRe.IsMatch(rest) || ToolMarkup.BarLooseRe.IsMatch(rest)
                    || ToolMarkup.BarWrapRe.IsMatch(rest) || rest.IndexOf("calls", StringComparison.OrdinalIgnoreCase) >= 0;
                if (dirty)
                {
                    leak++;
                    if (leak <= 4) leakSample += "\n   BODY=[" + body + "] -> [" + rest + "]";
                }
            }
            Check("Z10 随机 fuzz 不泄漏 (500 次)", leak == 0 && miss == 0, "leak=" + leak + " miss=" + miss + " || " + leakSample);
        }

        // Z11~Z20) 工具调用「JSON 写坏 / 漏写标记」不再泄漏进正文（2026-10-09 修复）
        {
            // Z11 截断块（有 begin 没 end）：坏 JSON 必须整块丢弃，绝不补发
            var cr = OpenAiAdapter.Process("", "开始\n" + OpenAiAdapter.START
                + "[{\"name\":\"read_file\",\"arguments\":{\"path\":\"Get-Date", tools);
            Check("Z11 截断块不泄漏",
                cr.content.IndexOf("read_file", StringComparison.Ordinal) < 0
                && cr.content.IndexOf("arguments", StringComparison.Ordinal) < 0,
                "content='" + cr.content + "' err=" + cr.toolError);
        }
        {
            // Z12 漏写标记：裸 JSON 数组也要被识别为工具调用
            var cr = OpenAiAdapter.Process("", "好的。\n[{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.txt\"}}]", tools);
            Check("Z12 裸 JSON 数组识别",
                cr.toolCalls != null && cr.toolCalls.Count == 1
                && cr.content.IndexOf("read_file", StringComparison.Ordinal) < 0,
                "calls=" + (cr.toolCalls?.Count ?? -1) + " content='" + cr.content + "'");
        }
        {
            // Z13 漏写标记 + {"tool_calls":[...]} 包装
            var cr = OpenAiAdapter.Process("", "好的。\n{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.txt\"}}]}", tools);
            Check("Z13 裸 tool_calls 包装识别",
                cr.toolCalls != null && cr.toolCalls.Count == 1
                && cr.content.IndexOf("tool_calls", StringComparison.Ordinal) < 0,
                "calls=" + (cr.toolCalls?.Count ?? -1) + " content='" + cr.content + "'");
        }
        {
            // Z14 结构性修复：批量调用每个元素少一个 }
            var cr = OpenAiAdapter.Process("", OpenAiAdapter.START
                + "[{\"name\":\"read_file\",\"arguments\":{\"path\":\"a\"},{\"name\":\"read_file\",\"arguments\":{\"path\":\"b\"}}]"
                + OpenAiAdapter.END, tools);
            Check("Z14 缺元素 } 补括号", cr.toolCalls != null && cr.toolCalls.Count == 2,
                "calls=" + (cr.toolCalls?.Count ?? -1) + " content='" + cr.content + "'");
        }
        {
            // Z15 缺最后的 ]
            var cr = OpenAiAdapter.Process("", OpenAiAdapter.START
                + "[{\"name\":\"read_file\",\"arguments\":{\"path\":\"a\"}}" + OpenAiAdapter.END, tools);
            Check("Z15 缺 ] 仍可识别", cr.toolCalls != null && cr.toolCalls.Count == 1,
                "calls=" + (cr.toolCalls?.Count ?? -1));
        }
        {
            // Z16 文档现象 A（args\": 多余转义 + 被截断）绝不泄漏
            var cr = OpenAiAdapter.Process("", "片段。\n" + OpenAiAdapter.START
                + "[{\"name\": \"run_mcp\", \"arguments\": {\"args\\\": {\\\"code\\\":\\\"async () => {", tools);
            Check("Z16 文档现象A 不泄漏",
                cr.content.IndexOf("arguments", StringComparison.Ordinal) < 0
                && cr.content.IndexOf("run_mcp", StringComparison.Ordinal) < 0,
                "content='" + cr.content + "' err=" + cr.toolError);
        }
        {
            // Z17 未声明工具时，正文里的 JSON 不许被误判、也不许被吞
            var none = new HashSet<string>(StringComparer.Ordinal);
            var cr = OpenAiAdapter.Process("", "这是数据：\n[{\"name\":\"Alice\",\"arguments\":{\"x\":1}}]", none);
            Check("Z17 无工具声明时裸 JSON 当正文",
                (cr.toolCalls == null || cr.toolCalls.Count == 0) && cr.content.Contains("Alice"),
                "calls=" + (cr.toolCalls?.Count ?? -1) + " content='" + cr.content + "'");
        }
        {
            // Z18 正文里讨论标记仍原样保留（不能因为防泄漏而吞正文）
            var cr = OpenAiAdapter.Process("", "这段标记 " + OpenAiAdapter.START + " 是协议规定。", tools);
            Check("Z18 正文讨论标记不被吞", cr.content.Contains("是协议规定"),
                "content='" + cr.content + "'");
        }
        {
            // Z19 流式：声明工具时裸 JSON 也要被 hold（不得逐字上屏）
            string bare = "好的。\n[{\"name\":\"read_file\",\"arguments\":{\"path\":\"a\"}}]";
            int emitted0 = 0; bool tm = false;
            for (int i = 1; i <= bare.Length; i++)
            {
                string ct = bare.Substring(0, i);
                if (tm) continue;
                int emitBare = ToolMarkup.SafeEmitEnd(ct, 32, true);
                if (emitBare > emitted0) emitted0 = emitBare;
                if (ToolMarkup.EarliestSignal(ct, true) >= 0) tm = true;
            }
            Check("Z19 流式裸 JSON 被 hold",
                bare.Substring(0, emitted0).IndexOf("name", StringComparison.Ordinal) < 0,
                "shown='" + bare.Substring(0, emitted0) + "'");
        }
        {
            // Z20 流式收尾补发：写坏的块必须被 CutSpans 裁掉
            string full = "开始\n" + OpenAiAdapter.START + "[{\"name\":\"read_file\",\"arguments\":{\"path\":\"半";
            var cr = OpenAiAdapter.Process("", full, tools);
            int emitted = ToolMarkup.SafeEmitEnd(full, 32, true);
            string tail = ToolMarkup.StripStray(full.Substring(emitted));
            tail = OpenAiAdapter.CutSpans(tail, emitted, cr.droppedSpans);
            Check("Z20 流式收尾不补发坏块",
                tail.IndexOf("arguments", StringComparison.Ordinal) < 0
                && tail.IndexOf("read_file", StringComparison.Ordinal) < 0,
                "emitted=" + emitted + " tail='" + tail + "'");
        }

        // Z21~Z24) 输入/附件上限保护（2026-10-09）：超长 prompt 中段截断 + 图片附件只留最近 N 张
        {
            // Z21 超长 prompt 中段截断：必须保留「系统指令」与最近的对话，并插入省略标记
            var longMsg = new string('A', 4000) + "尾部最新内容";
            var req = (JObj)Json.TryParse("{\"messages\":[{\"role\":\"system\",\"content\":\"你是助手\"},{\"role\":\"user\",\"content\":\"" + longMsg + "\"}],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"read_file\"}}]}");
            var r = PromptBuilder.Build(req, true, 1200, 0);
            Check("Z21 超长 prompt 中段截断",
                r.truncated && r.text.Length <= 1200
                && r.text.Contains("已自动省略") && r.text.Contains("尾部最新内容")
                && r.text.Contains("【系统指令】"),
                "len=" + r.text.Length + " truncated=" + r.truncated);
        }
        {
            // Z22 未超长：原样不动、不截断
            var req = (JObj)Json.TryParse("{\"messages\":[{\"role\":\"user\",\"content\":\"你好\"}]}");
            var r = PromptBuilder.Build(req, true, 100000, 0);
            Check("Z22 未超长不改写", !r.truncated && r.text.Contains("你好"), "len=" + r.text.Length);
        }
        {
            // Z23 图片附件超上限：只保留最近的 N 张，记录被略过数
            string img = "{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,iVBORw0KGgo=\"}}";
            var arr = new StringBuilder();
            for (int i = 0; i < 5; i++) { if (i > 0) arr.Append(','); arr.Append(img); }
            var req = (JObj)Json.TryParse("{\"messages\":[{\"role\":\"user\",\"content\":[" + arr.ToString() + ",{\"type\":\"text\",\"text\":\"看图\"}]}]}");
            var r = PromptBuilder.Build(req, true, 0, 2);
            Check("Z23 附件超上限只留最近 N 张",
                r.attachments.Count == 2 && r.omittedImages == 3,
                "kept=" + r.attachments.Count + " omitted=" + r.omittedImages);
        }
        {
            // Z24 会话模式（stateless=false）同样受上限保护
            var longMsg = new string('B', 3000) + "最后的提问";
            var req = (JObj)Json.TryParse("{\"messages\":[{\"role\":\"user\",\"content\":\"" + longMsg + "\"}]}");
            var r = PromptBuilder.Build(req, false, 900, 0);
            Check("Z24 会话模式同样受上限保护",
                r.truncated && r.text.Length <= 900 && r.text.Contains("最后的提问"),
                "len=" + r.text.Length + " truncated=" + r.truncated);
        }

        Say("--- 自检结果: " + _pass + " 通过 / " + _fail + " 失败 ---");

        try
        {
            string path = Path.Combine(Log.Dir ?? Path.GetTempPath(), "selftest.log");
            File.WriteAllText(path, string.Join(Environment.NewLine, _lines) + Environment.NewLine, Encoding.UTF8);
            Say("结果已写入: " + path);
        }
        catch { }

        return _fail == 0 ? 0 : 1;
    }
}
