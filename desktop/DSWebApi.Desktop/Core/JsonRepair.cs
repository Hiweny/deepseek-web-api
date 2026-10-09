using System.Text;

namespace DSWebApi.Desktop.Core;

/// <summary>
/// 工具调用 JSON 的**结构性修复**（对齐 dsh-deepseek-web-login 的 rebuildToolCallJson / repairJsonText）。
///
/// 为什么需要它：模型写出的工具调用经常只是「JSON 语法坏了」——
///   1) 漏写闭合括号（批量调用时每个元素少一个 `}`，实测事故）
///   2) `]` / `}` 闭合顺序错乱（arguments 写成数组后没收尾就写了 `}`）
///   3) 字符串外的全角标点（`"command"： "..."`）
/// 这些只要结构被补平，调用就能被完整恢复；补不平就得整块丢弃——**绝不能漏进正文**。
///
/// ⚠️ 安全闸门：扫描结束时若仍在字符串内（典型的「流被截断」特征），一律返回 null ——
/// 此时补括号会造出一条**被截断的命令**，宁可拒绝，也绝不交出半条命令。
/// </summary>
public static class JsonRepair
{
    /// <summary>把「字符串外」的全角标点归一为半角；字符串内容（含中文标点）一律不动。</summary>
    public static string NormalizeFullWidth(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        bool has = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '：' || c == '＂' || c == '，' || c == '｛' || c == '｝'
                || c == '［' || c == '］' || c == '“' || c == '”' || c == '〔' || c == '〕')
            { has = true; break; }
        }
        if (!has) return s;

        var sb = new StringBuilder(s.Length);
        bool inStr = false, esc = false;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (inStr)
            {
                sb.Append(ch);
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            switch (ch)
            {
                case '“': case '”': sb.Append('"'); inStr = true; break;
                case '＂': sb.Append('"'); inStr = true; break;
                case '：': sb.Append(':'); break;
                case '，': sb.Append(','); break;
                case '｛': case '〔': sb.Append('{'); break;
                case '｝': case '〕': sb.Append('}'); break;
                case '［': sb.Append('['); break;
                case '］': sb.Append(']'); break;
                case '"': sb.Append(ch); inStr = true; break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 栈引导重排：遇到不匹配的闭合符时**插入缺失的容器闭合**，只插入括号、绝不改写字符串内容。
    /// 返回 null 表示「不可安全修复」（截断 / 结构太乱 / 插入次数超限）。
    /// </summary>
    public static string TryRebuild(string text)
    {
        if (text == null) return null;
        string t = text.Trim();
        if (t.Length == 0) return null;
        if (t[0] != '{' && t[0] != '[') return null;

        var sb = new StringBuilder(t.Length + 16);
        var stack = new List<char>();
        bool inStr = false, esc = false;
        int insertions = 0;

        for (int i = 0; i < t.Length; i++)
        {
            char ch = t[i];
            if (inStr)
            {
                sb.Append(ch);
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; sb.Append(ch); continue; }
            if (ch == '{' || ch == '[') { stack.Add(ch); sb.Append(ch); continue; }
            if (ch == '}' || ch == ']')
            {
                char want = ch == '}' ? '{' : '[';
                while (stack.Count > 0 && stack[stack.Count - 1] != want)
                {
                    if (insertions >= 8) return null;
                    sb.Append(stack[stack.Count - 1] == '{' ? '}' : ']');
                    stack.RemoveAt(stack.Count - 1);
                    insertions++;
                }
                if (stack.Count == 0) return null;   // 多余的闭合符 → 结构不可信
                stack.RemoveAt(stack.Count - 1);
                sb.Append(ch);
                continue;
            }
            if (ch == ',')
            {
                // 元素级漏 `}`：处于 tool_calls 数组的元素层级、栈顶是调用对象、
                // 且逗号后面紧跟下一个 `{"name":` —— 说明模型忘了写这个元素的 `}`。
                int bi = stack.IndexOf('[');
                if ((bi == 0 || bi == 1) && stack.Count - bi - 1 == 1 && stack[stack.Count - 1] == '{'
                    && NextStartsCallElement(t, i + 1))
                {
                    if (insertions >= 8) return null;
                    sb.Append('}');
                    stack.RemoveAt(stack.Count - 1);
                    insertions++;
                }
                sb.Append(ch);
                continue;
            }
            sb.Append(ch);
        }

        if (inStr) return null;              // 安全闸门：截断（仍在字符串内）
        if (insertions > 8) return null;
        while (stack.Count > 0)
        {
            sb.Append(stack[stack.Count - 1] == '{' ? '}' : ']');
            stack.RemoveAt(stack.Count - 1);
        }
        return sb.ToString();
    }

    private static bool NextStartsCallElement(string t, int i)
    {
        while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
        return StartsAt(t, i, "{\"name\":") || StartsAt(t, i, "{\"name\" :") || StartsAt(t, i, "{ \"name\"");
    }

    private static bool StartsAt(string t, int i, string p)
        => i >= 0 && i + p.Length <= t.Length && string.CompareOrdinal(t, i, p, 0, p.Length) == 0;

    /// <summary>
    /// 从 <paramref name="start"/>（指向 `{` 或 `[`）做配平扫描，返回闭合符之后的索引；
    /// 配平不了（截断）返回 -1。会跟踪字符串与转义。
    /// </summary>
    public static int BalancedEnd(string t, int start)
    {
        if (t == null || start < 0 || start >= t.Length) return -1;
        char first = t[start];
        if (first != '{' && first != '[') return -1;
        int depth = 0;
        bool inStr = false, esc = false;
        for (int i = start; i < t.Length; i++)
        {
            char ch = t[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; continue; }
            if (ch == '{' || ch == '[') depth++;
            else if (ch == '}' || ch == ']')
            {
                depth--;
                if (depth == 0) return i + 1;
                if (depth < 0) return -1;
            }
        }
        return -1;
    }
}
