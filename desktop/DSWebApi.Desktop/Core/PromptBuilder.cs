using System.Text;

namespace DSWebApi.Desktop.Core;

/// <summary>
/// 把 OpenAI Chat Completions 请求转换为「注入到官网输入框」的文本，并提取附件。
/// 与 APK PromptBuilder.java 对齐（含规则 1-10 的工具调用提示词）。
/// </summary>
public static class PromptBuilder
{
    public const string TOOL_START = "<|tool\u2581calls\u2581begin|>";
    public const string TOOL_END = "<|tool\u2581calls\u2581end|>";
    public const string TOOL_OUT_B = "<\uFF5Ctool\u2581outputs\u2581begin\uFF5C><\uFF5Ctool\u2581output\u2581begin\uFF5C>";
    public const string TOOL_OUT_E = "<\uFF5Ctool\u2581output\u2581end\uFF5C><\uFF5Ctool\u2581outputs\u2581end\uFF5C>";

    public sealed class Attachment
    {
        public readonly string Name, Mime, Base64;
        public Attachment(string name, string mime, string base64) { Name = name; Mime = mime; Base64 = base64; }
    }

    public sealed class Result
    {
        public string text = "";
        public readonly List<Attachment> attachments = new List<Attachment>();
        public bool hasTools = false;
        /// <summary>本次请求声明的工具名（用于甄别「正文里解释标记」的误判）。</summary>
        public readonly List<string> toolNames = new List<string>();
    }

    public static Result Build(JObj req, bool stateless)
    {
        var r = new Result();
        var messages = req.Arr("messages");
        if (messages == null || messages.Count == 0) { r.text = ""; return r; }

        var tools = req.Arr("tools");
        r.hasTools = tools != null && tools.Count > 0;
        CollectToolNames(tools, r.toolNames);

        string toolBlock = r.hasTools ? BuildToolBlock(tools, req) : "";
        string formatBlock = ResponseFormatBlock(req.Obj("response_format"));

        if (stateless)
        {
            r.text = BuildStateless(messages, toolBlock, formatBlock, r);
            return r;
        }

        // 会话模式：定位 delta 起点（最后一条 assistant 之后）
        int deltaStart = 0;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i] as JObj;
            if (m != null && m.Str("role") == "assistant") { deltaStart = i + 1; break; }
        }

        var sb = new StringBuilder();
        var sysBuf = new StringBuilder();
        var body = new StringBuilder();

        for (int i = deltaStart; i < messages.Count; i++)
        {
            var m = messages[i] as JObj;
            if (m == null) continue;
            string role = m.Str("role");
            if (role == "system")
            {
                string t = ContentToText(m.Get("content"), r, false);
                if (t.Length > 0) { if (sysBuf.Length > 0) sysBuf.Append("\n\n"); sysBuf.Append(t); }
            }
            else if (role == "user")
            {
                string t = ContentToText(m.Get("content"), r, true);
                if (t.Length > 0) { if (body.Length > 0) body.Append("\n\n"); body.Append(t); }
            }
            else if (role == "assistant") AppendAssistant(m, body, r);
            else if (role == "tool" || role == "function")
            {
                string t = ContentToText(m.Get("content"), r, false);
                if (body.Length > 0) body.Append("\n\n");
                body.Append(TOOL_OUT_B).Append(t).Append(TOOL_OUT_E);
            }
        }

        bool hasSysContent = sysBuf.Length > 0;
        if (hasSysContent || toolBlock.Length > 0 || formatBlock.Length > 0)
        {
            sb.Append("【系统指令】\n");
            if (hasSysContent) sb.Append(sysBuf);
            if (toolBlock.Length > 0) { if (hasSysContent) sb.Append("\n\n"); sb.Append(toolBlock); }
            if (formatBlock.Length > 0) sb.Append("\n\n").Append(formatBlock);
            sb.Append("\n\n");
        }
        sb.Append(body);
        r.text = sb.ToString().Trim();
        return r;
    }

    private static string BuildStateless(JArr messages, string toolBlock, string formatBlock, Result r)
    {
        var sb = new StringBuilder();
        var sys = new StringBuilder();
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i] as JObj;
            if (m == null) continue;
            if (m.Str("role") == "system")
            {
                string t = ContentToText(m.Get("content"), r, false);
                if (t.Length > 0) { if (sys.Length > 0) sys.Append("\n\n"); sys.Append(t); }
            }
        }
        string sysAll = sys.ToString().Trim();
        var body = new StringBuilder();
        if (sysAll.Length > 0) body.Append("【系统指令】\n").Append(sysAll);
        if (toolBlock.Length > 0) { if (body.Length > 0) body.Append("\n\n"); body.Append(toolBlock); }
        if (formatBlock.Length > 0) { if (body.Length > 0) body.Append("\n\n"); body.Append(formatBlock); }
        if (body.Length > 0) body.Append("\n\n");

        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i] as JObj;
            if (m == null) continue;
            string role = m.Str("role");
            if (role == "system") continue;
            if (role == "assistant") AppendAssistant(m, body, r);
            else if (role == "tool" || role == "function")
            {
                string t = ContentToText(m.Get("content"), r, false);
                if (body.Length > 0) body.Append("\n\n");
                body.Append(TOOL_OUT_B).Append(t).Append(TOOL_OUT_E);
            }
            else
            {
                string t = ContentToText(m.Get("content"), r, true);
                if (t.Length > 0) { if (body.Length > 0) body.Append("\n\n"); body.Append(t); }
            }
        }
        return body.ToString().Trim();
    }

    private static void AppendAssistant(JObj m, StringBuilder body, Result r)
    {
        string t = ContentToText(m.Get("content"), r, false);
        var calls = m.Arr("tool_calls");
        var s = new StringBuilder();
        if (t.Length > 0) s.Append(t);
        if (calls != null && calls.Count > 0)
        {
            var arr = new StringBuilder();
            for (int i = 0; i < calls.Count; i++)
            {
                var tc = calls[i] as JObj;
                var fn = tc?.Obj("function");
                if (fn == null) continue;
                if (arr.Length > 0) arr.Append(", ");
                arr.Append("{\"name\": ").Append(Json.Quote(fn.Str("name"))).Append(", \"arguments\": ")
                   .Append(SafeArgs(fn.Str("arguments"))).Append("}");
            }
            if (arr.Length > 0) s.Append(TOOL_START).Append("[").Append(arr).Append("]").Append(TOOL_END);
        }
        if (s.Length > 0)
        {
            if (body.Length > 0) body.Append("\n\n");
            body.Append(s);
        }
    }

    private static string SafeArgs(string args)
    {
        string a = (args ?? "").Trim();
        if (a.Length == 0) return "{}";
        if (Json.TryParse(a) is JObj || Json.TryParse(a) is JArr) return a;
        return Json.Quote(a);
    }

    /// <summary>content 可能是字符串，也可能是 parts 数组；text 部分拼文本，image/file 部分收集为附件。</summary>
    private static string ContentToText(JVal content, Result r, bool collectImages)
    {
        if (content == null) return "";
        if (content is JStr js) return js.V.Trim();
        if (!(content is JArr parts)) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i] as JObj;
            if (p == null) continue;
            string type = p.Str("type");
            if (type == "text" || type == "input_text")
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(p.Str("text"));
            }
            else if (type == "image_url")
            {
                var iu = p.Obj("image_url");
                string url = iu != null ? iu.Str("url") : p.Str("image_url");
                var a = ToAttachment(url, "image");
                if (a != null) { r.attachments.Add(a); if (collectImages) sb.Append("[已附加图片 ").Append(a.Name).Append("]"); }
            }
            else if (type == "input_image")
            {
                string url = p.Str("image_url");
                var a = ToAttachment(url, "image");
                if (a != null) { r.attachments.Add(a); if (collectImages) sb.Append("[已附加图片 ").Append(a.Name).Append("]"); }
            }
            else if (type == "file" || type == "input_file")
            {
                var f = p.Obj("file") ?? p;
                string name = f.Str("filename", f.Str("name", "file"));
                string data = f.Str("file_data", f.Str("data", ""));
                if (data.StartsWith("data:"))
                {
                    var du = DataUrl.Parse(data);
                    if (du != null) { r.attachments.Add(new Attachment(name, du.Mime, du.B64)); sb.Append("[已附加文件 ").Append(name).Append("]"); }
                }
                else if (data.StartsWith("http"))
                {
                    var a = Downloader.Fetch(data, name);
                    if (a != null) { r.attachments.Add(a); sb.Append("[已附加文件 ").Append(name).Append("]"); }
                }
            }
        }
        return sb.ToString().Trim();
    }

    private static Attachment ToAttachment(string url, string kind)
    {
        if (string.IsNullOrEmpty(url)) return null;
        string stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        if (url.StartsWith("data:"))
        {
            var du = DataUrl.Parse(url);
            if (du == null) return null;
            string ext = du.Mime.Contains("png") ? ".png"
                : (du.Mime.Contains("jpeg") || du.Mime.Contains("jpg")) ? ".jpg"
                : du.Mime.Contains("gif") ? ".gif"
                : du.Mime.Contains("webp") ? ".webp" : "";
            return new Attachment(kind + "-" + stamp + ext, du.Mime, du.B64);
        }
        if (url.StartsWith("http"))
        {
            return Downloader.Fetch(url, kind + "-" + stamp);
        }
        return null;
    }

    private static string ResponseFormatBlock(JObj rf)
    {
        if (rf == null) return "";
        string type = rf.Str("type");
        if (type == "json_object") return "请直接输出合法的 JSON 对象，不要包含 markdown 代码块标记或解释性文字。";
        if (type == "json_schema")
        {
            var js = rf.Obj("json_schema");
            return "请以 JSON 形式输出，并遵守以下 JSON Schema：\n" + (js == null ? "{}" : js.ToJson());
        }
        return "";
    }

    /// <summary>收集工具名（兼容 function.name 与顶层 name 两种形态）。</summary>
    private static void CollectToolNames(JArr tools, List<string> outNames)
    {
        if (tools == null) return;
        for (int i = 0; i < tools.Count; i++)
        {
            var t = tools[i] as JObj;
            if (t == null) continue;
            var fn = t.Obj("function");
            string n = (fn != null ? fn.Str("name", "") : t.Str("name", "")).Trim();
            if (n.Length > 0 && !outNames.Contains(n)) outNames.Add(n);
        }
    }

    private static string BuildToolBlock(JArr tools, JObj req)
    {
        var sb = new StringBuilder();
        sb.Append("你可以使用以下工具：\n");
        var names = new List<string>();
        for (int i = 0; i < tools.Count; i++)
        {
            var t = tools[i] as JObj;
            if (t == null) continue;
            var fn = t.Obj("function");
            if (fn == null) continue;
            string name = fn.Str("name");
            if (name.Length == 0) continue;
            names.Add(name);
            string prms = fn.Str("parameters", "{}");
            string desc = fn.Str("description", "").Trim();
            sb.Append("- **").Append(name).Append("** (function):\n");
            sb.Append("  - 调用方法: `").Append(TOOL_START).Append("[{\"name\": \"").Append(name)
              .Append("\", \"arguments\": ").Append(prms).Append("}]").Append(TOOL_END).Append("`\n");
            sb.Append("  - 简要说明:\n~~~markdown\n  ").Append(desc.Length == 0 ? "无描述" : desc).Append("\n~~~\n");
        }

        sb.Append("\n**工具调用格式 — 请严格遵守：**\n\n");
        sb.Append("将 JSON 数组包裹在工具调用标记中：\n\n");
        sb.Append(TOOL_START).Append("[{\"name\": \"工具名\", \"arguments\": {参数JSON}}]").Append(TOOL_END).Append("\n\n");
        sb.Append("**规则：**\n\n");
        sb.Append("1. 决定调用工具时，响应中**只允许**出现工具调用文本本身，禁止任何解释、前缀、总结、问候语。\n");
        sb.Append("2. JSON 数组必须以 `").Append(TOOL_START).Append("` 开头、以 `").Append(TOOL_END).Append("` 结尾，完整包裹。\n");
        sb.Append("3. 所有工具调用放在**一个** JSON 数组中，多个用逗号分隔。\n");
        sb.Append("4. 输出 `").Append(TOOL_END).Append("` 后立即停止，不要添加后续文字。\n");
        sb.Append("5. 不要用 markdown 代码块包裹工具调用。\n");
        sb.Append("6. 字符串参数值用**双引号**（标准 JSON）。\n");
        sb.Append("7. 不要将工具调用或最终回复放进思考内容里。\n");
        sb.Append("8. `arguments` 必须是一个 **JSON 对象**，不要把它整体再写成字符串（禁止 `\"arguments\": \"{...}\"` 这种写法）。\n");
        sb.Append("9. 若某个参数值本身就是一段 JSON 文本，则该值内部的双引号只需要转义**一层**，请严格照下方示例的写法，不要漏转义、也不要多转义。\n");
        sb.Append("10. 严禁在正文、思考、代码块或示例中原样写出工具调用标记本身。"
                + "若需要说明格式，请用「工具调用开始标记 / 结束标记」这样的文字描述；"
                + "正文里出现真实标记会被系统当成工具调用，导致你后面的内容被截断。\n");
        if (names.Count > 0)
        {
            string a = names[0];
            sb.Append("\n**示例**（调用一个工具）：\n");
            sb.Append(TOOL_START).Append("[{\"name\": \"").Append(a).Append("\", \"arguments\": {}}]").Append(TOOL_END);
            if (names.Count >= 2)
            {
                sb.Append("\n\n**示例**（并行调用两个工具）：\n");
                sb.Append(TOOL_START).Append("[{\"name\": \"").Append(names[0]).Append("\", \"arguments\": {}}, {\"name\": \"")
                  .Append(names[1]).Append("\", \"arguments\": {}}]").Append(TOOL_END);
            }
        }
        sb.Append("\n**示例**（参数值里含 JSON 字符串时，注意只转义一层）：\n");
        sb.Append(TOOL_START)
          .Append("[{\"name\": \"example\", \"arguments\": {\"params\": {\"files\": \"[{\\\"field_name\\\": \\\"a.txt\\\"}]\"}}}]")
          .Append(TOOL_END);
        string tc = req.Str("tool_choice", "");
        if (tc == "required") sb.Append("\n\n**注意：你必须调用一个或多个工具。**");
        return sb.ToString();
    }

    /// <summary>data:image/png;base64,xxxx</summary>
    public sealed class DataUrl
    {
        public string Mime, B64;

        public static DataUrl Parse(string s)
        {
            try
            {
                int comma = s.IndexOf(',');
                if (comma < 0) return null;
                string head = s.Substring(5, comma - 5); // after "data:"
                string b64 = s.Substring(comma + 1);
                string mime = head;
                int semi = head.IndexOf(';');
                if (semi >= 0) mime = head.Substring(0, semi);
                if (mime.Length == 0) mime = "application/octet-stream";
                return new DataUrl { Mime = mime, B64 = b64.Trim() };
            }
            catch (Exception) { return null; }
        }
    }
}
