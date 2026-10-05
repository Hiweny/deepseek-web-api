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
            Check("H 正文解释标记不误判且不吞内容",
                cr.toolCalls == null && cr.content == body && cr.toolError == "NOT_A_TOOL_CALL",
                "tc=" + (cr.toolCalls?.Count) + " len=" + cr.content.Length + "/" + body.Length);
        }

        // I) 代码块内的标记
        {
            string body = "示例：\n```\n"
                + OpenAiAdapter.START + "[{\"name\":\"xxx\",\"arguments\":{}}]" + OpenAiAdapter.END
                + "\n```\n以上。";
            var cr = OpenAiAdapter.Process("", body, tools);
            Check("I 代码块内标记不误判且不吞内容",
                cr.toolCalls == null && cr.content == body,
                "tc=" + (cr.toolCalls?.Count) + " len=" + cr.content.Length);
        }

        // J) 标记内 JSON 非法 → 原样返回全文
        {
            string body = OpenAiAdapter.START + "[not a json]" + OpenAiAdapter.END + "尾部说明";
            var cr = OpenAiAdapter.Process("", body, tools);
            Check("J 非法内容不误判且保留全文", cr.toolCalls == null && cr.content == body,
                "tc=" + (cr.toolCalls?.Count));
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
                cr.toolCalls == null && cr.content == body, "tc=" + (cr.toolCalls?.Count));
        }

        // R) 无 tools 列表时：代码块内的标记不当成工具调用
        {
            string body = "示例：\n```\n"
                + OpenAiAdapter.START + "[{\"name\":\"foo\",\"arguments\":{}}]" + OpenAiAdapter.END
                + "\n```\n以上。";
            var cr = OpenAiAdapter.Process("", body, null);
            Check("R 无列表：代码块内标记不误判",
                cr.toolCalls == null && cr.content == body, "tc=" + (cr.toolCalls?.Count));
        }

        // P) 过度转义（多一层）也能还原成对象，而不是丢给下游一个字符串
        {
            string doubly = Q(Q("{\"path\":\"/a\"}"));
            string p = "[{\"name\":\"read_file\",\"arguments\":" + doubly + "}]";
            Check("P 过度转义 arguments 仍还原为对象", ChainOk(p, tools, out var e7), e7);
        }

        // S) 关键回归：bridge.js 内嵌资源必须能载入（曾因逻辑名不匹配导致 0 字节、API 全部 NO_BRIDGE）
        {
            Log.Init();
            string js = Program.LoadEmbedded("bridge.js");
            Check("S bridge.js 内嵌资源可载入且非空",
                js.Length > 5000 && js.Contains("window.DSKB") && js.Contains("__DSWB_LOADED"),
                "bytes=" + js.Length);
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
