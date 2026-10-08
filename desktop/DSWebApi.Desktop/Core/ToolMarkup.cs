using System;
using System.Text.RegularExpressions;

namespace DSWebApi.Desktop.Core
{
    /// <summary>
    /// 工具调用标记（DeepSeek 原生特殊 token）的**容错识别与残片剥离**。
    ///
    /// 背景：提示词让模型以文本形式输出
    ///     &lt;|tool&#x2581;calls&#x2581;begin|&gt;[{...}]&lt;|tool&#x2581;calls&#x2581;end|&gt;
    /// 但模型经常"写残"：开始标签写丢只剩闭合标签、只剩后半截（voke&gt;）、
    /// 退化成无竖线的 &lt;tool_calls_begin&gt;、竖线用全角 ｜、分隔符用 _ 或空格……
    /// 旧实现只做精确等值匹配，这些残片就漏进了正文。
    ///
    /// 设计对齐 dsh 的 stripStrayToolMarkup 思路，把容错集中在本类：
    ///   1) 强特征（含 tool+calls+begin/end，竖线可选）—— 任意位置都剥
    ///   2) 竖线包裹的短标签残片 —— 剥
    ///   3) 独占行的退化形态（voke&gt; / calls&gt; / &lt;/ calls&gt;）—— 剥（避免误伤行内讨论）
    /// 正常正文讨论（如"在 HTML 里 &lt;invoke&gt; 只是普通文字"）不受影响。
    /// </summary>
    public static class ToolMarkup
    {
        // 竖线（半角 | 或全角 ｜）+ 允许其后的空格
        private const string BAR = "[|\\uFF5C]\\s*";
        // 竖线整体作为**可选分组**。注意：直接写 BAR+"?" 只会让末尾 \s* 变懒惰，竖线仍必需，
        // 于是无竖线的退化形态 <tool_calls_begin> 匹配不到（实测踩过）。
        private const string BAROPT = "(?:[|\\uFF5C]\\s*)?";
        // 内部分隔（_ / ▁ / 空格 混用）
        private const string SEP = "[\\s_\\u2581]*";

        /// <summary>强特征：含 tool + calls + begin/end（竖线可选，开闭任意，大小写不敏感）。</summary>
        public static readonly Regex StrongRe = new Regex(
            "<\\s*/?\\s*" + BAROPT + "\\s*tool" + SEP + "calls?" + SEP + "(?:begin|end)" + SEP + BAROPT + ">",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>开始标记（宽容）：&lt;|tool&#x2581;calls&#x2581;begin|&gt; 及各种变体。</summary>
        public static readonly Regex BeginRe = new Regex(
            "<\\s*/?\\s*" + BAROPT + "\\s*tool" + SEP + "calls?" + SEP + "begin" + SEP + BAROPT + ">",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>结束标记（宽容）：&lt;|tool&#x2581;calls&#x2581;end|&gt; 及各种变体。</summary>
        public static readonly Regex EndRe = new Regex(
            "<\\s*/?\\s*" + BAROPT + "\\s*tool" + SEP + "calls?" + SEP + "end" + SEP + BAROPT + ">",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>JSON 工具调用信号：{"tool_calls":  …</summary>
        public static readonly Regex JsonSigRe = new Regex(
            "\\{\\s*\"tool_calls\"\\s*:", RegexOptions.Compiled);

        /// <summary>竖线包裹的短标签残片：&lt;/|...|&gt; 或 &lt;|...|&gt;（内部是标识符串）。</summary>
        public static readonly Regex BarWrapRe = new Regex(
            "<\\s*/?\\s*" + BAR + "[A-Za-z0-9_\\s]{0,40}" + BAR + ">",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>弱特征（仅独占一行才剥）：voke&gt; / calls&gt; / &lt;/ calls&gt; 等退化形态。</summary>
        public static readonly Regex WeakLineRe = new Regex(
            "(?:^|\\n)[ \\t]*(?:in)?voke\\s*>[ \\t]*(?=\\n|$)" +
            "|(?:^|\\n)[ \\t]*calls?\\s*>[ \\t]*(?=\\n|$)" +
            "|(?:^|\\n)[ \\t]*</?[ \\t]*" + "[|\\uFF5C]?" + "[ \\t]*(?:tool[\\s_\\u2581]*)?calls?[\\s_\\u2581]*(?:begin|end)?[ \\t]*" + "[|\\uFF5C]?" + "[ \\t]*>[ \\t]*(?=\\n|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>尾部"未完成标记前缀"（如 &lt; / &lt;/ / &lt;| / &lt;/|tool）——只 hold，不剥。</summary>
        private static readonly Regex PartialTailRe = new Regex(
            "<\\s*/?\\s*(?:[|\\uFF5C]\\s*)?(?:tool[\\s_\\u2581]*)?calls?[\\s_\\u2581]*(?:begin|end)?[\\s_\\u2581]*[|\\uFF5C]?\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>返回最早的"标记信号"起始位置；没有则 -1。</summary>
        public static int EarliestSignal(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            int best = -1;
            var m = StrongRe.Match(text);
            if (m.Success) best = m.Index;
            var j = JsonSigRe.Match(text);
            if (j.Success && (best < 0 || j.Index < best)) best = j.Index;
            return best;
        }

        /// <summary>末尾未闭合的标记前缀（可能是跨包标记的半截），返回其起始位置；没有则 -1。</summary>
        public static int PartialTailStart(string text, int maxLen = 72)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            int from = Math.Max(0, text.Length - maxLen);
            int i = text.LastIndexOf('<');
            if (i < from) return -1;
            string frag = text.Substring(i);
            if (frag.IndexOf('>') >= 0) return -1;   // 已闭合
            return PartialTailRe.IsMatch(frag) ? i : -1;
        }

        /// <summary>
        /// 流式：返回**可以安全上屏**的长度 —— 标记信号或半截标记之前的部分。
        /// </summary>
        public static int SafeEmitEnd(string text, int hold = 32)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int m = EarliestSignal(text);
            if (m >= 0) return m;
            int p = PartialTailStart(text);
            if (p >= 0) return p;
            int safe = text.Length - hold;
            return safe < 0 ? 0 : safe;
        }

        /// <summary>剥掉正文里残留的工具标记碎片（强特征任意位置 + 独占行弱特征）。</summary>
        public static string StripStray(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string low = text.ToLowerInvariant();
            bool fast = low.IndexOf("voke>", StringComparison.Ordinal) >= 0
                || low.IndexOf("calls>", StringComparison.Ordinal) >= 0
                || low.IndexOf("calls_begin", StringComparison.Ordinal) >= 0
                || low.IndexOf("calls_end", StringComparison.Ordinal) >= 0
                || low.IndexOf("tool\u2581calls", StringComparison.Ordinal) >= 0
                || low.IndexOf("tool_calls", StringComparison.Ordinal) >= 0
                || low.IndexOf("tool calls", StringComparison.Ordinal) >= 0
                || low.IndexOf("|tool", StringComparison.Ordinal) >= 0
                || low.IndexOf("\uFF5Ctool", StringComparison.Ordinal) >= 0;
            if (!fast) return text;
            string outText = StrongRe.Replace(text, "");
            outText = BarWrapRe.Replace(outText, "");
            outText = WeakLineRe.Replace(outText, KeepLeadingNewline);
            return outText;
        }

        private static string KeepLeadingNewline(Match m)
        {
            return m.Value.StartsWith("\n", StringComparison.Ordinal) ? "\n" : "";
        }
    }
}
