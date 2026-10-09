package com.hiweny.dswebapi;

/**
 * 工具调用 JSON 的<b>结构性修复</b>（对齐桌面端 {@code Core/JsonRepair.cs} 与
 * dsh-deepseek-web-login 的 rebuildToolCallJson / repairJsonText）。
 *
 * <p>为什么需要它：模型写出的工具调用经常只是「JSON 语法坏了」——
 * <ol>
 *   <li>漏写闭合括号（批量调用时每个元素少一个 {@code \}}）</li>
 *   <li>{@code ]} / {@code \}} 闭合顺序错乱（arguments 写成数组后没收尾就写了 {@code \}}）</li>
 *   <li>字符串外的全角标点（{@code "command"： "..."}）</li>
 * </ol>
 * 只要结构被补平，调用就能被完整恢复；补不平就得整块丢弃 —— <b>绝不能漏进正文</b>。
 *
 * <p>⚠️ 安全闸门：扫描结束时若仍在字符串内（典型的「流被截断」特征），一律返回 null ——
 * 此时补括号会造出一条被截断的命令，宁可拒绝，也绝不交出半条命令。
 */
public final class JsonRepair {

    private JsonRepair() {}

    /** 把「字符串外」的全角标点归一为半角；字符串内容（含中文标点）一律不动。 */
    public static String normalizeFullWidth(String s) {
        if (s == null || s.isEmpty()) return s;
        boolean has = false;
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '\uFF1A' || c == '\uFF02' || c == '\uFF0C' || c == '\uFF5B' || c == '\uFF5D'
                    || c == '\uFF3B' || c == '\uFF3D' || c == '\u201C' || c == '\u201D'
                    || c == '\u3014' || c == '\u3015') { has = true; break; }
        }
        if (!has) return s;

        StringBuilder sb = new StringBuilder(s.length());
        boolean inStr = false, esc = false;
        for (int i = 0; i < s.length(); i++) {
            char ch = s.charAt(i);
            if (inStr) {
                sb.append(ch);
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            switch (ch) {
                case '\u201C': case '\u201D': sb.append('"'); inStr = true; break;
                case '\uFF02': sb.append('"'); inStr = true; break;
                case '\uFF1A': sb.append(':'); break;
                case '\uFF0C': sb.append(','); break;
                case '\uFF5B': case '\u3014': sb.append('{'); break;
                case '\uFF5D': case '\u3015': sb.append('}'); break;
                case '\uFF3B': sb.append('['); break;
                case '\uFF3D': sb.append(']'); break;
                case '"': sb.append(ch); inStr = true; break;
                default: sb.append(ch); break;
            }
        }
        return sb.toString();
    }

    /**
     * 栈引导重排：遇到不匹配的闭合符时<b>插入缺失的容器闭合</b>，只插入括号、绝不改写字符串内容。
     * 返回 null 表示「不可安全修复」（截断 / 结构太乱 / 插入次数超限）。
     */
    public static String tryRebuild(String text) {
        if (text == null) return null;
        String t = text.trim();
        if (t.isEmpty()) return null;
        if (t.charAt(0) != '{' && t.charAt(0) != '[') return null;

        StringBuilder sb = new StringBuilder(t.length() + 16);
        java.util.List<Character> stack = new java.util.ArrayList<Character>();
        boolean inStr = false, esc = false;
        int insertions = 0;

        for (int i = 0; i < t.length(); i++) {
            char ch = t.charAt(i);
            if (inStr) {
                sb.append(ch);
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') { inStr = true; sb.append(ch); continue; }
            if (ch == '{' || ch == '[') { stack.add(ch); sb.append(ch); continue; }
            if (ch == '}' || ch == ']') {
                char want = ch == '}' ? '{' : '[';
                while (!stack.isEmpty() && stack.get(stack.size() - 1) != want) {
                    if (insertions >= 8) return null;
                    sb.append(stack.get(stack.size() - 1) == '{' ? '}' : ']');
                    stack.remove(stack.size() - 1);
                    insertions++;
                }
                if (stack.isEmpty()) return null;   // 多余的闭合符 → 结构不可信
                stack.remove(stack.size() - 1);
                sb.append(ch);
                continue;
            }
            if (ch == ',') {
                // 元素级漏 }：处于 tool_calls 数组的元素层级、栈顶是调用对象、
                // 且逗号后面紧跟下一个 {"name": —— 说明模型忘了写这个元素的 }。
                int bi = stack.indexOf('[');
                if ((bi == 0 || bi == 1) && stack.size() - bi - 1 == 1 && stack.get(stack.size() - 1) == '{'
                        && nextStartsCallElement(t, i + 1)) {
                    if (insertions >= 8) return null;
                    sb.append('}');
                    stack.remove(stack.size() - 1);
                    insertions++;
                }
                sb.append(ch);
                continue;
            }
            sb.append(ch);
        }

        if (inStr) return null;              // 安全闸门：截断（仍在字符串内）
        if (insertions > 8) return null;
        while (!stack.isEmpty()) {
            sb.append(stack.get(stack.size() - 1) == '{' ? '}' : ']');
            stack.remove(stack.size() - 1);
        }
        return sb.toString();
    }

    private static boolean nextStartsCallElement(String t, int i) {
        while (i < t.length() && Character.isWhitespace(t.charAt(i))) i++;
        return startsAt(t, i, "{\"name\":") || startsAt(t, i, "{\"name\" :") || startsAt(t, i, "{ \"name\"");
    }

    private static boolean startsAt(String t, int i, String p) {
        return i >= 0 && i + p.length() <= t.length() && t.regionMatches(i, p, 0, p.length());
    }

    /**
     * 配平扫描：从 start（须为 '{' 或 '['）开始，返回与起始括号配对的闭合位置下一位；
     * 未配平（截断）返回 -1。字符串内的括号不计入。
     */
    public static int balancedEnd(String text, int start) {
        if (text == null || start < 0 || start >= text.length()) return -1;
        char open = text.charAt(start);
        if (open != '{' && open != '[') return -1;
        int depth = 0;
        boolean inStr = false, esc = false;
        for (int i = start; i < text.length(); i++) {
            char c = text.charAt(i);
            if (inStr) {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') {
                depth--;
                if (depth == 0) return i + 1;
            }
        }
        return -1;
    }
}
