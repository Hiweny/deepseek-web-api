using System.Text;

namespace DSWebApi.Desktop.Core;

/// <summary>
/// 宽松 JSON 解析器（带回溯），专门用于解析「模型输出的工具调用」。
/// 与 APK 的 LooseJson.java 行为 1:1 对齐。
/// </summary>
public sealed class LooseJson
{
    private const int BUDGET = 1500000;
    private const int MAX_CAND = 1500;

    private readonly string s;
    private readonly int n;
    private int budget;

    private LooseJson(string s)
    {
        this.s = s;
        this.n = s.Length;
        this.budget = BUDGET;
    }

    private sealed class R
    {
        public readonly JVal v;
        public readonly int end;
        /// <summary>代价：解析过程中把「裸引号」当成字符串字面量吞掉的次数，越少越可信。</summary>
        public readonly int cost;
        public R(JVal v, int e, int c) { this.v = v; this.end = e; this.cost = c; }
    }

    /* ================= 入口 ================= */

    /// <summary>解析入口：返回 JObj / JArr / JStr / JNum / JBool / JNull，失败返回 null。</summary>
    public static JVal Parse(string text)
    {
        if (text == null) return null;
        JVal v = ParseOnce(text);
        if (v != null) return v;
        string alt = StripBackslashesBeforeQuotes(text);
        if (alt != text) v = ParseOnce(alt);
        return v;
    }

    /// <summary>单次解析（不做降级）：优先从头开始，其次从第一个 { 或 [ 开始。</summary>
    private static JVal ParseOnce(string text)
    {
        if (text == null) return null;
        string t = text.Trim();
        if (t.Length == 0) return null;
        JVal v = ParseFrom(t, 0);
        if (v != null) return v;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '{' || c == '[') return ParseFrom(t, i);
        }
        return null;
    }

    private static JVal ParseFrom(string t, int start)
    {
        var p = new LooseJson(t);
        List<R> cands = p.Vals(start);
        R best = null;
        int bestScore = int.MinValue;
        // 第一优先：能吃满整段文本，且「内部像 JSON 的字符串值」结构最自洽（括号配平）的解析
        foreach (R c in cands)
        {
            if (p.SkipWs(c.end) < p.n) continue;
            int sc = JsonScore(c.v);
            if (best == null || sc > bestScore || (sc == bestScore && c.cost < best.cost))
            {
                best = c;
                bestScore = sc;
            }
        }
        if (best == null)
        {   // 没有能吃满全文的：退化为「吞掉裸引号最少」
            foreach (R c in cands)
            {
                if (best == null || c.cost < best.cost) best = c;
            }
        }
        return best == null ? null : best.v;
    }

    /// <summary>打分：字符串值若「像 JSON」，括号配平 +1，不配平 -1。</summary>
    private static int JsonScore(JVal v)
    {
        int score = 0;
        try
        {
            if (v is JObj o)
            {
                foreach (string k in o.Keys.ToList())
                {
                    JVal ch = o.Get(k);
                    if (ch is JStr st) score += JsonLike(st.V);
                    else if (ch != null) score += JsonScore(ch);
                }
            }
            else if (v is JArr a)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    JVal ch = a[i];
                    if (ch is JStr st) score += JsonLike(st.V);
                    else if (ch != null) score += JsonScore(ch);
                }
            }
        }
        catch (Exception) { }
        return score;
    }

    private static int JsonLike(string s)
    {
        if (s == null) return 0;
        string t = s.Trim();
        if (t.Length < 2) return 0;
        char c0 = t[0];
        if (c0 != '{' && c0 != '[') return 0;
        return Balanced(t) ? 1 : -1;
    }

    /// <summary>括号配平检查（不区分是否在字符串内，作为启发式足够）。</summary>
    private static bool Balanced(string s)
    {
        int d = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '{' || c == '[') d++;
            else if (c == '}' || c == ']') { d--; if (d < 0) return false; }
        }
        return d == 0;
    }

    /// <summary>
    /// 把「值本身是被转义成字符串的 JSON」还原成未转义的 JSON 文本。
    /// 仅当该值含反斜杠、去掉一层转义后以 { 或 [ 开头、且确实能解析成 JSON 时才替换。
    /// </summary>
    public static JVal NormalizeJsonStrings(JVal v)
    {
        try
        {
            if (v is JObj o)
            {
                foreach (string k in o.Keys.ToList())
                {
                    JVal child = o.Get(k);
                    if (child is JStr cs) o.Set(k, new JStr(FixJsonString(cs.V)));
                    else if (child != null) NormalizeJsonStrings(child);
                }
            }
            else if (v is JArr a)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    JVal child = a[i];
                    if (child is JStr cs) a[i] = new JStr(FixJsonString(cs.V));
                    else if (child != null) NormalizeJsonStrings(child);
                }
            }
        }
        catch (Exception) { }
        return v;
    }

    private static string FixJsonString(string s)
    {
        if (s == null) return s;
        string st = s.Trim();
        if (st.Length < 2) return s;
        char c0 = st[0];
        if (c0 != '{' && c0 != '[') return s;

        // 1) 本身就是「严格合法」的 JSON（嵌套 JSON 已正确转义）→ 原样保留，绝不能剥层
        if (StrictOk(st)) return s;

        // 2) 过度转义：去掉一层转义后变成严格合法 → 采用
        if (s.IndexOf('\\') >= 0)
        {
            string u = UnescapeOneLevel(s);
            if (u != s && StrictOk(u.Trim())) return u;
        }

        // 3) 转义不足（内层引号裸露）：宽松解析恢复结构，再重新序列化成严格合法文本交还
        JVal v = ParseOnce(st);
        if (v is JObj || v is JArr) return v.ToJson();
        return s;
    }

    /// <summary>是否为标准的严格合法 JSON（等价于下游客户端的 JSON.parse）。</summary>
    private static bool StrictOk(string t) => Json.StrictOk(t);

    /// <summary>去掉一层转义：\X -> X（未知转义保持原样）。</summary>
    public static string UnescapeOneLevel(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char e = s[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\'': sb.Append('\''); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    default: sb.Append('\\').Append(e); break;
                }
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>去掉所有「紧挨引号的转义反斜杠」（应对把结构引号也一起转义的输出）。</summary>
    private static string StripBackslashesBeforeQuotes(string s)
    {
        string prev = null;
        string cur = s;
        int guard = 0;
        while (cur != prev && guard++ < 6)
        {
            prev = cur;
            cur = cur.Replace("\\\"", "\"");
        }
        return cur;
    }

    /* ================= 基础 ================= */

    private int SkipWs(int pos)
    {
        while (pos < n && char.IsWhiteSpace(s[pos])) pos++;
        return pos;
    }

    private static bool IsDelim(char c) => char.IsWhiteSpace(c) || c == ',' || c == '}' || c == ']';

    private List<R> Vals(int pos)
    {
        var outp = new List<R>();
        if (--budget < 0) return outp;
        pos = SkipWs(pos);
        if (pos >= n) return outp;
        char c = s[pos];
        if (c == '{') return ObjVals(pos);
        if (c == '[') return ArrVals(pos);
        if (c == '"' || c == '\'') return StrVals(pos);
        return LitVals(pos);
    }

    /* ================= 对象 ================= */

    private List<R> ObjVals(int pos)
    {
        var outp = new List<R>();
        var partial = new List<R> { new R(new JObj(), pos + 1, 0) };
        while (partial.Count > 0 && outp.Count <= MAX_CAND && budget > 0)
        {
            var next = new List<R>();
            foreach (R cur in partial)
            {
                var baseObj = (JObj)cur.v;
                int p = SkipWs(cur.end);
                if (p < n && s[p] == '}') { outp.Add(new R(baseObj, p + 1, cur.cost)); continue; }
                foreach (R key in Vals(p))
                {
                    if (!(key.v is JStr)) continue;
                    int q = SkipWs(key.end);
                    if (q >= n || s[q] != ':') continue;
                    foreach (R val in Vals(q + 1))
                    {
                        var o = Copy(baseObj);
                        try { o.Set(((JStr)key.v).V, val.v ?? JNull.I); }
                        catch (Exception) { continue; }
                        int r2 = SkipWs(val.end);
                        int cost = cur.cost + key.cost + val.cost;
                        if (r2 < n && s[r2] == ',') next.Add(new R(o, r2 + 1, cost));
                        else if (r2 < n && s[r2] == '}') outp.Add(new R(o, r2 + 1, cost));
                        if (next.Count > MAX_CAND) break;
                    }
                    if (next.Count > MAX_CAND) break;
                }
            }
            partial = next;
        }
        return outp;
    }

    /* ================= 数组 ================= */

    private List<R> ArrVals(int pos)
    {
        var outp = new List<R>();
        var partial = new List<R> { new R(new JArr(), pos + 1, 0) };
        while (partial.Count > 0 && outp.Count <= MAX_CAND && budget > 0)
        {
            var next = new List<R>();
            foreach (R cur in partial)
            {
                var baseArr = (JArr)cur.v;
                int p = SkipWs(cur.end);
                if (p < n && s[p] == ']') { outp.Add(new R(baseArr, p + 1, cur.cost)); continue; }
                foreach (R val in Vals(p))
                {
                    var a = CopyArr(baseArr);
                    try { a.Add(val.v ?? JNull.I); } catch (Exception) { continue; }
                    int r2 = SkipWs(val.end);
                    int cost = cur.cost + val.cost;
                    if (r2 < n && s[r2] == ',') next.Add(new R(a, r2 + 1, cost));
                    else if (r2 < n && s[r2] == ']') outp.Add(new R(a, r2 + 1, cost));
                    if (next.Count > MAX_CAND) break;
                }
            }
            partial = next;
        }
        return outp;
    }

    /* ================= 字符串（歧义枚举的关键） ================= */

    private List<R> StrVals(int pos)
    {
        var strong = new List<R>();
        var weak = new List<R>();
        char q = s[pos];
        var sb = new StringBuilder();
        int i = pos + 1;
        int lit = 0;   // 已当作字面量吞掉的裸引号数量
        while (i < n)
        {
            if (--budget < 0) break;
            char c = s[i];
            if (c == '\\')
            {
                int adv = 0;
                char ch = Unescape(i, ref adv);
                if (adv <= i) { sb.Append(c); i++; continue; }
                sb.Append(ch);
                i = adv;
                continue;
            }
            if (c == q)
            {
                int j = SkipWs(i + 1);
                char nx = j < n ? s[j] : '\0';
                if (nx == ',' || nx == '}' || nx == ']' || nx == '\0') strong.Add(new R(new JStr(sb.ToString()), i + 1, lit));
                else if (nx == ':') weak.Add(new R(new JStr(sb.ToString()), i + 1, lit));
                // 其余情况视为「字符串内部的裸引号」，继续按字面量往下扫描（代价 +1）
                sb.Append(c);
                i++;
                lit++;
                continue;
            }
            if (c == '\n' || c == '\r') { sb.Append(' '); i++; continue; }
            sb.Append(c);
            i++;
        }
        strong.Add(new R(new JStr(sb.ToString()), n, lit));   // 未正常闭合时，以文本末尾兜底
        var outp = new List<R>();
        outp.AddRange(strong);
        outp.AddRange(weak);
        if (outp.Count > MAX_CAND) outp = outp.GetRange(0, MAX_CAND);
        return outp;
    }

    /// <summary>处理一个转义序列，返回其对应字符，adv 为下一个位置。</summary>
    private char Unescape(int i, ref int adv)
    {
        if (i + 1 >= n) { adv = i + 1; return '\\'; }
        char e = s[i + 1];
        switch (e)
        {
            case 'n': adv = i + 2; return '\n';
            case 't': adv = i + 2; return '\t';
            case 'r': adv = i + 2; return '\r';
            case 'b': adv = i + 2; return '\b';
            case 'f': adv = i + 2; return '\f';
            case '/': adv = i + 2; return '/';
            case '"': adv = i + 2; return '"';
            case '\'': adv = i + 2; return '\'';
            case '\\': adv = i + 2; return '\\';
            case 'u':
                if (i + 6 <= n)
                {
                    try
                    {
                        char ch = (char)Convert.ToInt32(s.Substring(i + 2, 4), 16);
                        adv = i + 6;
                        return ch;
                    }
                    catch (Exception) { }
                }
                adv = i + 2;
                return 'u';
            default:
                adv = i + 2;
                return e;   // 未知转义：保留原字符，不报错
        }
    }

    /* ================= 字面量 ================= */

    private List<R> LitVals(int pos)
    {
        var outp = new List<R>();
        int i = pos;
        while (i < n && !IsDelim(s[i])) i++;
        if (i <= pos) return outp;
        string tok = s.Substring(pos, i - pos);
        JVal v = null;
        if (tok == "true") v = JBool.T;
        else if (tok == "false") v = JBool.F;
        else if (tok == "null") v = JNull.I;
        else
        {
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(tok, "^-?\\d+$"))
                    v = JNum.Of(long.Parse(tok, System.Globalization.CultureInfo.InvariantCulture));
                else if (double.TryParse(tok, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    v = JNum.Of(d);
            }
            catch (Exception) { }
        }
        if (v != null) outp.Add(new R(v, i, 0));
        return outp;
    }

    /* ================= 复制 ================= */

    private static JObj Copy(JObj o)
    {
        var c = new JObj();
        foreach (string k in o.Keys)
        {
            try { c.Set(k, o.Get(k)); } catch (Exception) { }
        }
        return c;
    }

    private static JArr CopyArr(JArr a)
    {
        var c = new JArr();
        for (int i = 0; i < a.Count; i++) c.Add(a[i]);
        return c;
    }
}
