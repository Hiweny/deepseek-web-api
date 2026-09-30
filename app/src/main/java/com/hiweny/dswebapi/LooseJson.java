package com.hiweny.dswebapi;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;

/**
 * 宽松 JSON 解析器（带回溯），专门用于解析「模型输出的工具调用」。
 *
 * <p>模型在输出工具参数时，经常会遇到「参数值本身是一段 JSON 字符串」的情况，例如
 * {@code {"params": {"files": "[{\"field_name\":\"files[]\"}]"}}}：
 * 此时字符串内部的双引号需要转义一层（{@code \"}），但模型常常写得不一致
 * （少转义成裸 {@code "}、多转义成 {@code \\"}），导致标准 JSON 解析失败、
 * 整段工具调用被当成正文泄漏出来。
 *
 * <p>处理策略（逐级降级）：
 * <ol>
 *   <li>直接解析（标准 JSON，绝大多数情况命中）；</li>
 *   <li>回溯解析：当一个裸引号既可能是字符串结束、也可能是字符串内部的字面引号时，
 *       两种分支都枚举出来，由外层结构能否正常闭合来判定，可修复「少转义」；</li>
 *   <li>去掉引号前的多余反斜杠后再解析，可修复「结构引号也被转义」的多转义；</li>
 *   <li>{@link #normalizeJsonStrings(Object)} 把「被过度转义成字符串的 JSON 值」还原，
 *       避免下游客户端拿到一堆多余反斜杠。</li>
 * </ol>
 */
public final class LooseJson {

    private static final int BUDGET = 1500000;
    private static final int MAX_CAND = 1500;

    private final String s;
    private final int n;
    private int budget;

    private LooseJson(String s) {
        this.s = s;
        this.n = s.length();
        this.budget = BUDGET;
    }

    private static final class R {
        final Object v;
        final int end;
        /** 代价：解析过程中把「裸引号」当成字符串字面量吞掉的次数，越少越可信。 */
        final int cost;
        R(Object v, int e, int c) { this.v = v; this.end = e; this.cost = c; }
    }

    /* ================= 入口 ================= */

    /** 解析入口：返回 JSONObject / JSONArray / String / Number / Boolean / JSONObject.NULL，失败返回 null。 */
    public static Object parse(String text) {
        if (text == null) return null;
        Object v = parseOnce(text);
        if (v != null) return v;
        String alt = stripBackslashesBeforeQuotes(text);
        if (!alt.equals(text)) v = parseOnce(alt);
        return v;
    }

    /** 单次解析（不做降级）：优先从头开始，其次从第一个 { 或 [ 开始。 */
    private static Object parseOnce(String text) {
        if (text == null) return null;
        String t = text.trim();
        if (t.isEmpty()) return null;
        Object v = parseFrom(t, 0);
        if (v != null) return v;
        for (int i = 0; i < t.length(); i++) {
            char c = t.charAt(i);
            if (c == '{' || c == '[') return parseFrom(t, i);
        }
        return null;
    }

    private static Object parseFrom(String t, int start) {
        LooseJson p = new LooseJson(t);
        List<R> cands = p.vals(start);
        R best = null;
        int bestScore = Integer.MIN_VALUE;
        // 第一优先：能吃满整段文本，且「内部像 JSON 的字符串值」结构最自洽（括号配平）的解析
        for (R c : cands) {
            if (p.skipWs(c.end) < p.n) continue;
            int sc = jsonScore(c.v);
            if (best == null || sc > bestScore || (sc == bestScore && c.cost < best.cost)) {
                best = c;
                bestScore = sc;
            }
        }
        if (best == null) {   // 没有能吃满全文的：退化为「吞掉裸引号最少」
            for (R c : cands) {
                if (best == null || c.cost < best.cost) best = c;
            }
        }
        return best == null ? null : best.v;
    }

    /** 打分：字符串值若「像 JSON」，括号配平 +1，不配平 -1；越高说明该解析越自洽。 */
    private static int jsonScore(Object v) {
        int score = 0;
        try {
            if (v instanceof JSONObject) {
                JSONObject o = (JSONObject) v;
                Iterator<String> it = o.keys();
                List<String> ks = new ArrayList<>();
                while (it.hasNext()) ks.add(it.next());
                for (String k : ks) {
                    Object ch = o.get(k);
                    if (ch instanceof String) score += jsonLike((String) ch);
                    else if (ch != null) score += jsonScore(ch);
                }
            } else if (v instanceof JSONArray) {
                JSONArray a = (JSONArray) v;
                for (int i = 0; i < a.length(); i++) {
                    Object ch = a.get(i);
                    if (ch instanceof String) score += jsonLike((String) ch);
                    else if (ch != null) score += jsonScore(ch);
                }
            }
        } catch (Exception ignored) { }
        return score;
    }

    private static int jsonLike(String s) {
        if (s == null) return 0;
        String t = s.trim();
        if (t.length() < 2) return 0;
        char c0 = t.charAt(0);
        if (c0 != '{' && c0 != '[') return 0;
        return balanced(t) ? 1 : -1;
    }

    /** 括号配平检查（不区分是否在字符串内，作为启发式足够）。 */
    private static boolean balanced(String s) {
        int d = 0;
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '{' || c == '[') d++;
            else if (c == '}' || c == ']') { d--; if (d < 0) return false; }
        }
        return d == 0;
    }

    /**
     * 把「值本身是被转义成字符串的 JSON」还原成未转义的 JSON 文本。
     * 仅当该值含反斜杠、去掉一层转义后以 { 或 [ 开头、且确实能解析成 JSON 时才替换，
     * 因此对普通文本参数零影响。
     */
    public static Object normalizeJsonStrings(Object v) {
        try {
            if (v instanceof JSONObject) {
                JSONObject o = (JSONObject) v;
                List<String> keys = new ArrayList<>();
                Iterator<String> it = o.keys();
                while (it.hasNext()) keys.add(it.next());
                for (String k : keys) {
                    Object child = o.get(k);
                    if (child instanceof String) o.put(k, fixJsonString((String) child));
                    else if (child != null) normalizeJsonStrings(child);
                }
            } else if (v instanceof JSONArray) {
                JSONArray a = (JSONArray) v;
                for (int i = 0; i < a.length(); i++) {
                    Object child = a.get(i);
                    if (child instanceof String) a.put(i, fixJsonString((String) child));
                    else if (child != null) normalizeJsonStrings(child);
                }
            }
        } catch (Exception ignored) { }
        return v;
    }

    private static String fixJsonString(String s) {
        if (s == null || s.indexOf('\\') < 0) return s;
        String u = unescapeOneLevel(s);
        if (u.equals(s)) return s;
        String t = u.trim();
        if (t.length() < 2) return s;
        char c0 = t.charAt(0);
        if (c0 != '{' && c0 != '[') return s;
        Object probe = parseOnce(t);
        return probe != null ? u : s;
    }

    /** 去掉一层转义：\X -> X（未知转义保持原样）。 */
    private static String unescapeOneLevel(String s) {
        StringBuilder sb = new StringBuilder(s.length());
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '\\' && i + 1 < s.length()) {
                char e = s.charAt(++i);
                switch (e) {
                    case 'n': sb.append('\n'); break;
                    case 't': sb.append('\t'); break;
                    case 'r': sb.append('\r'); break;
                    case '"': sb.append('"'); break;
                    case '\'': sb.append('\''); break;
                    case '\\': sb.append('\\'); break;
                    case '/': sb.append('/'); break;
                    default: sb.append('\\').append(e);
                }
            } else {
                sb.append(c);
            }
        }
        return sb.toString();
    }

    /** 去掉所有「紧挨引号的转义反斜杠」（应对把结构引号也一起转义的输出）。 */
    private static String stripBackslashesBeforeQuotes(String s) {
        String prev = null;
        String cur = s;
        int guard = 0;
        while (!cur.equals(prev) && guard++ < 6) {
            prev = cur;
            cur = cur.replace("\\\"", "\"");
        }
        return cur;
    }

    /* ================= 基础 ================= */

    private int skipWs(int pos) {
        while (pos < n && Character.isWhitespace(s.charAt(pos))) pos++;
        return pos;
    }

    private static boolean isDelim(char c) {
        return Character.isWhitespace(c) || c == ',' || c == '}' || c == ']';
    }

    private List<R> vals(int pos) {
        List<R> out = new ArrayList<>();
        if (--budget < 0) return out;
        pos = skipWs(pos);
        if (pos >= n) return out;
        char c = s.charAt(pos);
        if (c == '{') return objVals(pos);
        if (c == '[') return arrVals(pos);
        if (c == '"' || c == '\'') return strVals(pos);
        return litVals(pos);
    }

    /* ================= 对象 ================= */

    private List<R> objVals(int pos) {
        List<R> out = new ArrayList<>();
        List<R> partial = new ArrayList<>();
        partial.add(new R(new JSONObject(), pos + 1, 0));
        while (!partial.isEmpty() && out.size() <= MAX_CAND && budget > 0) {
            List<R> next = new ArrayList<>();
            for (R cur : partial) {
                JSONObject base = (JSONObject) cur.v;
                int p = skipWs(cur.end);
                if (p < n && s.charAt(p) == '}') { out.add(new R(base, p + 1, cur.cost)); continue; }
                for (R key : vals(p)) {
                    if (!(key.v instanceof String)) continue;
                    int q = skipWs(key.end);
                    if (q >= n || s.charAt(q) != ':') continue;
                    for (R val : vals(q + 1)) {
                        JSONObject o = copy(base);
                        try { o.put((String) key.v, val.v == null ? JSONObject.NULL : val.v); }
                        catch (Exception e) { continue; }
                        int r2 = skipWs(val.end);
                        int cost = cur.cost + key.cost + val.cost;
                        if (r2 < n && s.charAt(r2) == ',') next.add(new R(o, r2 + 1, cost));
                        else if (r2 < n && s.charAt(r2) == '}') out.add(new R(o, r2 + 1, cost));
                        if (next.size() > MAX_CAND) break;
                    }
                    if (next.size() > MAX_CAND) break;
                }
            }
            partial = next;
        }
        return out;
    }

    /* ================= 数组 ================= */

    private List<R> arrVals(int pos) {
        List<R> out = new ArrayList<>();
        List<R> partial = new ArrayList<>();
        partial.add(new R(new JSONArray(), pos + 1, 0));
        while (!partial.isEmpty() && out.size() <= MAX_CAND && budget > 0) {
            List<R> next = new ArrayList<>();
            for (R cur : partial) {
                JSONArray base = (JSONArray) cur.v;
                int p = skipWs(cur.end);
                if (p < n && s.charAt(p) == ']') { out.add(new R(base, p + 1, cur.cost)); continue; }
                for (R val : vals(p)) {
                    JSONArray a = copyArr(base);
                    try { a.put(val.v == null ? JSONObject.NULL : val.v); } catch (Exception e) { continue; }
                    int r2 = skipWs(val.end);
                    int cost = cur.cost + val.cost;
                    if (r2 < n && s.charAt(r2) == ',') next.add(new R(a, r2 + 1, cost));
                    else if (r2 < n && s.charAt(r2) == ']') out.add(new R(a, r2 + 1, cost));
                    if (next.size() > MAX_CAND) break;
                }
            }
            partial = next;
        }
        return out;
    }

    /* ================= 字符串（歧义枚举的关键） ================= */

    private List<R> strVals(int pos) {
        List<R> strong = new ArrayList<>();
        List<R> weak = new ArrayList<>();
        char q = s.charAt(pos);
        StringBuilder sb = new StringBuilder();
        int i = pos + 1;
        int lit = 0;   // 已当作字面量吞掉的裸引号数量
        while (i < n) {
            if (--budget < 0) break;
            char c = s.charAt(i);
            if (c == '\\') {
                int[] adv = new int[1];
                char ch = unescape(i, adv);
                if (adv[0] <= i) { sb.append(c); i++; continue; }
                sb.append(ch);
                i = adv[0];
                continue;
            }
            if (c == q) {
                int j = skipWs(i + 1);
                char nx = j < n ? s.charAt(j) : 0;
                if (nx == ',' || nx == '}' || nx == ']' || nx == 0) strong.add(new R(sb.toString(), i + 1, lit));
                else if (nx == ':') weak.add(new R(sb.toString(), i + 1, lit));
                // 其余情况视为「字符串内部的裸引号」，继续按字面量往下扫描（代价 +1）
                sb.append(c);
                i++;
                lit++;
                continue;
            }
            if (c == '\n' || c == '\r') { sb.append(' '); i++; continue; }
            sb.append(c);
            i++;
        }
        strong.add(new R(sb.toString(), n, lit));   // 未正常闭合时，以文本末尾兜底
        List<R> out = new ArrayList<>();
        out.addAll(strong);
        out.addAll(weak);
        if (out.size() > MAX_CAND) out = out.subList(0, MAX_CAND);
        return out;
    }

    /** 处理一个转义序列，返回其对应字符，adv[0] 为下一个位置。 */
    private char unescape(int i, int[] adv) {
        if (i + 1 >= n) { adv[0] = i + 1; return '\\'; }
        char e = s.charAt(i + 1);
        switch (e) {
            case 'n': adv[0] = i + 2; return '\n';
            case 't': adv[0] = i + 2; return '\t';
            case 'r': adv[0] = i + 2; return '\r';
            case 'b': adv[0] = i + 2; return '\b';
            case 'f': adv[0] = i + 2; return '\f';
            case '/': adv[0] = i + 2; return '/';
            case '"': adv[0] = i + 2; return '"';
            case '\'': adv[0] = i + 2; return '\'';
            case '\\': adv[0] = i + 2; return '\\';
            case 'u':
                if (i + 6 <= n) {
                    try {
                        char ch = (char) Integer.parseInt(s.substring(i + 2, i + 6), 16);
                        adv[0] = i + 6;
                        return ch;
                    } catch (Exception ignored) { }
                }
                adv[0] = i + 2;
                return 'u';
            default:
                adv[0] = i + 2;
                return e;   // 未知转义：保留原字符，不报错
        }
    }

    /* ================= 字面量 ================= */

    private List<R> litVals(int pos) {
        List<R> out = new ArrayList<>();
        int i = pos;
        while (i < n && !isDelim(s.charAt(i))) i++;
        if (i <= pos) return out;
        String tok = s.substring(pos, i);
        Object v = null;
        if ("true".equals(tok)) v = Boolean.TRUE;
        else if ("false".equals(tok)) v = Boolean.FALSE;
        else if ("null".equals(tok)) v = JSONObject.NULL;
        else {
            try {
                v = tok.matches("-?\\d+") ? (Object) Long.valueOf(tok) : (Object) Double.valueOf(tok);
            } catch (Exception ignored) { }
        }
        if (v != null) out.add(new R(v, i, 0));
        return out;
    }

    /* ================= 复制 ================= */

    private static JSONObject copy(JSONObject o) {
        JSONObject c = new JSONObject();
        Iterator<String> it = o.keys();
        while (it.hasNext()) {
            String k = it.next();
            try { c.put(k, o.get(k)); } catch (Exception ignored) { }
        }
        return c;
    }

    private static JSONArray copyArr(JSONArray a) {
        JSONArray c = new JSONArray();
        for (int i = 0; i < a.length(); i++) c.put(a.opt(i));
        return c;
    }
}
