package com.hiweny.dswebapi;

import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * 工具调用标记（DeepSeek 原生特殊 token）的<b>容错识别与残片剥离</b>。
 *
 * <p>背景：提示词让模型以文本形式输出
 * {@code <|tool▁calls▁begin|>[{...}]<|tool▁calls▁end|>}，
 * 但模型经常"写残"：开始标签写丢只剩闭合标签、只剩后半截（voke&gt;）、
 * 退化成无竖线的 &lt;tool_calls_begin&gt;、竖线用全角 ｜、分隔符用 _ 或空格……
 * 旧实现只做精确等值匹配，这些残片就漏进了正文。
 *
 * <p>与桌面端 {@code Core/ToolMarkup.cs} 对齐：强特征任意位置剥离 + 独占行弱特征剥离 +
 * 竖线包裹的宽松残片兜底剥离；此外支持「漏写标记的裸 JSON」识别。
 */
public final class ToolMarkup {

    private ToolMarkup() {}

    // 竖线（半角 | 或全角 ｜）+ 允许其后空格
    private static final String BAR = "[|\\uFF5C]\\s*";
    // 竖线整体作为**可选分组**（直接 BAR+"?" 只让 \s* 变懒、竖线仍必需）
    private static final String BAROPT = "(?:[|\\uFF5C]\\s*)?";
    // 内部分隔（_ / ▁ / 空格 / 竖线 混用）—— ★ 容忍标记内部多写的竖线（模型写残的一种）
    private static final String SEP = "[\\s_\\u2581|\\uFF5C]*";

    /** 强特征：含 tool + calls + begin/end（竖线可选，开闭任意，大小写不敏感）。 */
    public static final Pattern STRONG = Pattern.compile(
            "<\\s*/?\\s*" + BAROPT + "\\s*tool" + SEP + "calls?" + SEP + "(?:begin|end)" + SEP + BAROPT + ">",
            Pattern.CASE_INSENSITIVE);

    /** 开始标记（宽容）。 */
    public static final Pattern BEGIN = Pattern.compile(
            "<\\s*/?\\s*" + BAROPT + "\\s*tool" + SEP + "calls?" + SEP + "begin" + SEP + BAROPT + ">",
            Pattern.CASE_INSENSITIVE);

    /** 结束标记（宽容）。 */
    public static final Pattern END = Pattern.compile(
            "<\\s*/?\\s*" + BAROPT + "\\s*tool" + SEP + "calls?" + SEP + "end" + SEP + BAROPT + ">",
            Pattern.CASE_INSENSITIVE);

    /** JSON 工具调用信号：{"tool_calls": */
    public static final Pattern JSON_SIG = Pattern.compile("\\{\\s*\"tool_calls\"\\s*:");

    /** 裸 JSON 工具调用信号（**没有标记**时的兜底）：{"tool_calls":[  或  [{"name": */
    public static final Pattern BARE_JSON_SIG = Pattern.compile(
            "\\{\\s*\"(?:tool_calls?|function_calls?|calls)\"\\s*:|\\[\\s*\\{\\s*\"name\"\\s*:",
            Pattern.CASE_INSENSITIVE);

    /** 竖线包裹的短标签残片。 */
    public static final Pattern BAR_WRAP = Pattern.compile(
            "<\\s*/?\\s*" + BAR + "[A-Za-z0-9_\\s\\u2581|\\uFF5C]{0,40}" + BAR + ">",
            Pattern.CASE_INSENSITIVE);

    /** 竖线包裹的宽松残片：<| ... |>（关键词被插入字符打碎也能剥）。 */
    public static final Pattern BAR_LOOSE = Pattern.compile(
            "<[^<\\n]{0,24}?[|\\uFF5C][^<\\n]{0,120}?[|\\uFF5C]\\s*/?\\s*>",
            Pattern.CASE_INSENSITIVE);

    /** 弱特征（仅独占一行才剥）：voke&gt; / calls&gt; / &lt;/ calls&gt; 等退化形态。 */
    public static final Pattern WEAK_LINE = Pattern.compile(
            "(?:^|\\n)[ \\t]*(?:in)?voke\\s*>[ \\t]*(?=\\n|$)"
                    + "|(?:^|\\n)[ \\t]*calls?\\s*>[ \\t]*(?=\\n|$)"
                    + "|(?:^|\\n)[ \\t]*</?[ \\t]*[|\\uFF5C]?[ \\t]*(?:tool[\\s_\\u2581]*)?calls?[\\s_\\u2581]*(?:begin|end)?[ \\t]*[|\\uFF5C]?[ \\t]*>[ \\t]*(?=\\n|$)",
            Pattern.CASE_INSENSITIVE);

    /** 尾部"未完成标记前缀"（如 &lt; / &lt;/ / &lt;| / &lt;/|tool）——只 hold，不剥。 */
    private static final Pattern PARTIAL_TAIL = Pattern.compile(
            "<\\s*/?\\s*(?:[|\\uFF5C]\\s*)?(?:tool[\\s_\\u2581]*)?calls?[\\s_\\u2581]*(?:begin|end)?[\\s_\\u2581]*[|\\uFF5C]?\\s*$",
            Pattern.CASE_INSENSITIVE);

    /** 最早的"标记信号"起始位置；没有则 -1。 */
    public static int earliestSignal(String text) {
        return earliestSignal(text, false);
    }

    /** allowBareJson：客户端确实声明了工具时，**没有标记的裸 JSON** 也算信号。 */
    public static int earliestSignal(String text, boolean allowBareJson) {
        if (text == null || text.isEmpty()) return -1;
        int best = -1;
        Matcher m = STRONG.matcher(text);
        if (m.find()) best = m.start();
        Matcher j = JSON_SIG.matcher(text);
        if (j.find() && (best < 0 || j.start() < best)) best = j.start();
        if (allowBareJson) {
            Matcher b = BARE_JSON_SIG.matcher(text);
            if (b.find() && (best < 0 || b.start() < best)) best = b.start();
        }
        return best;
    }

    /** 末尾未闭合的标记前缀位置；没有则 -1。 */
    public static int partialTailStart(String text, int maxLen) {
        if (text == null || text.isEmpty()) return -1;
        int from = Math.max(0, text.length() - maxLen);
        int i = text.lastIndexOf('<');
        if (i < from) return -1;
        String frag = text.substring(i);
        if (frag.indexOf('>') >= 0) return -1;   // 已闭合
        return PARTIAL_TAIL.matcher(frag).find() ? i : -1;
    }

    /** 流式：可以安全上屏的长度（标记信号或半截标记之前）。 */
    public static int safeEmitEnd(String text, int hold) {
        return safeEmitEnd(text, hold, false);
    }

    public static int safeEmitEnd(String text, int hold, boolean allowBareJson) {
        if (text == null || text.isEmpty()) return 0;
        int m = earliestSignal(text, allowBareJson);
        if (m >= 0) return m;
        int p = partialTailStart(text, 72);
        if (p >= 0) return p;
        return Math.max(0, text.length() - hold);
    }

    /**
     * 文本是否「看起来就是一段工具调用 JSON」（以 { 或 [ 开头，且含 name/arguments/tool_calls 骨架）。
     * 判据故意保守：用来区分「写坏的调用块」与「正文里对标记的讨论」。
     */
    public static boolean looksLikeToolCallJson(String s) {
        if (s == null || s.isEmpty()) return false;
        int i = 0;
        while (i < s.length() && Character.isWhitespace(s.charAt(i))) i++;
        if (i >= s.length()) return false;
        char c = s.charAt(i);
        if (c != '{' && c != '[') return false;
        String t = s.substring(i).toLowerCase();
        return t.contains("\"name\"") || t.contains("tool_calls")
                || t.contains("\"arguments\"") || t.contains("\"parameters\"");
    }

    /** 从文本里移除若干 [start, end) 区间（区间须按起点升序、互不重叠）。 */
    public static String removeSpans(String text, List<int[]> spans) {
        if (text == null || text.isEmpty() || spans == null || spans.isEmpty()) return text;
        StringBuilder sb = new StringBuilder();
        int cur = 0;
        for (int[] sp : spans) {
            int s0 = sp[0], e0 = sp[1];
            if (s0 > cur) sb.append(text, cur, s0);
            if (e0 > cur) cur = e0;
        }
        if (cur < text.length()) sb.append(text, cur, text.length());
        return sb.toString();
    }

    /** 剥掉正文里残留的工具标记碎片（强特征任意位置 + 竖线宽松残片 + 独占行弱特征）。 */
    public static String stripStray(String text) {
        if (text == null || text.isEmpty()) return text;
        String low = text.toLowerCase();
        boolean fast = low.contains("voke>") || low.contains("calls>") || low.contains("calls")
                || low.contains("calls_begin") || low.contains("calls_end")
                || low.contains("tool\u2581calls") || low.contains("tool_calls")
                || low.contains("tool calls") || low.contains("|tool") || low.contains("\uFF5Ctool");
        if (!fast) return text;
        String out = STRONG.matcher(text).replaceAll("");
        out = BAR_WRAP.matcher(out).replaceAll("");
        out = BAR_LOOSE.matcher(out).replaceAll("");
        Matcher wm = WEAK_LINE.matcher(out);
        StringBuffer sb = new StringBuffer();
        while (wm.find()) {
            String v = wm.group();
            wm.appendReplacement(sb, Matcher.quoteReplacement(v.startsWith("\n") ? "\n" : ""));
        }
        wm.appendTail(sb);
        return sb.toString();
    }
}
