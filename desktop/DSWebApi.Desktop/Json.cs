using System.Globalization;
using System.Text;

namespace DSWebApi.Desktop;

/// <summary>JSON 解析异常。</summary>
public sealed class JEx : Exception
{
    public JEx(string m) : base(m) { }
}

/// <summary>极简 JSON 值模型（有序、紧凑序列化，语义对齐 org.json / JSON.parse）。</summary>
public abstract class JVal
{
    public abstract string ToJson();
    public override string ToString() => ToJson();

    public bool IsNull => this is JNull;
}

public sealed class JNull : JVal
{
    public static readonly JNull I = new JNull();
    private JNull() { }
    public override string ToJson() => "null";
}

public sealed class JBool : JVal
{
    public static readonly JBool T = new JBool(true);
    public static readonly JBool F = new JBool(false);
    public readonly bool V;
    private JBool(bool v) { V = v; }
    public static JBool Of(bool b) => b ? T : F;
    public override string ToJson() => V ? "true" : "false";
}

public sealed class JNum : JVal
{
    public readonly bool IsInt;
    public readonly long L;
    public readonly double D;

    private JNum(long l) { IsInt = true; L = l; D = l; }
    private JNum(double d) { IsInt = false; L = 0; D = d; }

    public static JNum Of(long l) => new JNum(l);
    public static JNum Of(double d)
    {
        if (!double.IsNaN(d) && !double.IsInfinity(d) && d == Math.Floor(d) && Math.Abs(d) < 1e15)
            return new JNum((long)d);
        return new JNum(d);
    }

    public override string ToJson()
    {
        if (IsInt) return L.ToString(CultureInfo.InvariantCulture);
        if (double.IsNaN(D) || double.IsInfinity(D)) return "null";
        if (D == Math.Floor(D) && Math.Abs(D) < 1e15) return ((long)D).ToString(CultureInfo.InvariantCulture);
        return D.ToString("R", CultureInfo.InvariantCulture);
    }

    public double AsDouble() => IsInt ? L : D;
}

public sealed class JStr : JVal
{
    public readonly string V;
    public JStr(string v) { V = v ?? ""; }
    public override string ToJson() => Json.Quote(V);
}

public sealed class JArr : JVal
{
    private readonly List<JVal> _l = new List<JVal>();
    public int Count => _l.Count;
    public JVal this[int i] { get => i >= 0 && i < _l.Count ? _l[i] : null; set { if (i >= 0 && i < _l.Count) _l[i] = value; } }
    public void Add(JVal v) => _l.Add(v ?? JNull.I);
    public List<JVal> Items => _l;
    public override string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < _l.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append((_l[i] ?? JNull.I).ToJson());
        }
        sb.Append(']');
        return sb.ToString();
    }
    public JArr Clone()
    {
        var c = new JArr();
        foreach (var v in _l) c.Add(v);
        return c;
    }
}

public sealed class JObj : JVal
{
    private readonly List<string> _keys = new List<string>();
    private readonly Dictionary<string, JVal> _map = new Dictionary<string, JVal>(StringComparer.Ordinal);

    public int Count => _keys.Count;
    public IReadOnlyList<string> Keys => _keys;

    public void Set(string k, JVal v)
    {
        if (k == null) return;
        if (!_map.ContainsKey(k)) _keys.Add(k);
        _map[k] = v ?? JNull.I;
    }

    public JObj Set(string k, string v) { Set(k, new JStr(v ?? "")); return this; }
    public JObj Set(string k, bool v) { Set(k, JBool.Of(v)); return this; }
    public JObj Set(string k, long v) { Set(k, JNum.Of(v)); return this; }
    public JObj Set(string k, int v) { Set(k, JNum.Of((long)v)); return this; }
    public JObj Set(string k, JVal v, bool _unused) { Set(k, v); return this; }

    public bool Has(string k) => k != null && _map.ContainsKey(k);
    public JVal Get(string k) => k != null && _map.TryGetValue(k, out var v) ? v : null;

    public JObj Obj(string k) => Get(k) as JObj;
    public JArr Arr(string k) => Get(k) as JArr;

    /// <summary>对齐 org.json 的 optString：非字符串会转成文本，null 返回默认值。</summary>
    public string Str(string k, string def = "")
    {
        var v = Get(k);
        if (v == null || v is JNull) return def;
        if (v is JStr s) return s.V;
        if (v is JNum n) return n.ToJson();
        if (v is JBool b) return b.V ? "true" : "false";
        return v.ToJson();
    }

    public bool Bool(string k, bool def = false)
    {
        var v = Get(k);
        if (v is JBool b) return b.V;
        if (v is JStr s)
        {
            if (string.Equals(s.V, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s.V, "false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return def;
    }

    public long Long(string k, long def = 0)
    {
        var v = Get(k);
        if (v is JNum n) return n.IsInt ? n.L : (long)n.D;
        if (v is JStr s && long.TryParse(s.V, NumberStyles.Any, CultureInfo.InvariantCulture, out var l)) return l;
        return def;
    }

    public int Int(string k, int def = 0) => (int)Long(k, def);

    public JObj Remove(string k)
    {
        if (k != null && _map.Remove(k)) _keys.Remove(k);
        return this;
    }

    public JObj Clone()
    {
        var c = new JObj();
        foreach (var k in _keys) c.Set(k, _map[k]);
        return c;
    }

    public override string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append('{');
        for (int i = 0; i < _keys.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var k = _keys[i];
            sb.Append(Json.Quote(k)).Append(':').Append((_map[k] ?? JNull.I).ToJson());
        }
        sb.Append('}');
        return sb.ToString();
    }
}

/// <summary>严格 JSON 解析（RFC 8259，等价于浏览器 JSON.parse）/ 序列化工具。</summary>
public static class Json
{
    /* ---------------- 解析 ---------------- */

    public static JVal ParseStrict(string s)
    {
        if (s == null) throw new JEx("null input");
        int i = 0;
        SkipWs(s, ref i);
        JVal v = ParseVal(s, ref i, 0);
        SkipWs(s, ref i);
        if (i != s.Length) throw new JEx("trailing characters at " + i);
        return v;
    }

    /// <summary>严格解析，失败返回 null。</summary>
    public static JVal TryParse(string s)
    {
        try { return ParseStrict(s); } catch (Exception) { return null; }
    }

    /// <summary>是否是对象/数组形态的严格合法 JSON（对齐 LooseJson.strictOk）。</summary>
    public static bool StrictOk(string s)
    {
        if (s == null) return false;
        try
        {
            var v = ParseStrict(s);
            return v is JObj || v is JArr;
        }
        catch (Exception) { return false; }
    }

    private const int MaxDepth = 200;

    private static JVal ParseVal(string s, ref int i, int depth)
    {
        if (depth > MaxDepth) throw new JEx("too deep");
        SkipWs(s, ref i);
        if (i >= s.Length) throw new JEx("unexpected end");
        char c = s[i];
        switch (c)
        {
            case '{': return ParseObj(s, ref i, depth);
            case '[': return ParseArr(s, ref i, depth);
            case '"': return new JStr(ParseString(s, ref i));
            case 't': Expect(s, ref i, "true"); return JBool.T;
            case 'f': Expect(s, ref i, "false"); return JBool.F;
            case 'n': Expect(s, ref i, "null"); return JNull.I;
            default: return ParseNumber(s, ref i);
        }
    }

    private static JVal ParseObj(string s, ref int i, int depth)
    {
        var o = new JObj();
        i++; // {
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return o; }
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new JEx("expected key at " + i);
            string k = ParseString(s, ref i);
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new JEx("expected ':' at " + i);
            i++;
            var v = ParseVal(s, ref i, depth + 1);
            o.Set(k, v);
            SkipWs(s, ref i);
            if (i >= s.Length) throw new JEx("unterminated object");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; return o; }
            throw new JEx("expected ',' or '}' at " + i);
        }
    }

    private static JVal ParseArr(string s, ref int i, int depth)
    {
        var a = new JArr();
        i++; // [
        SkipWs(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return a; }
        while (true)
        {
            var v = ParseVal(s, ref i, depth + 1);
            a.Add(v);
            SkipWs(s, ref i);
            if (i >= s.Length) throw new JEx("unterminated array");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; return a; }
            throw new JEx("expected ',' or ']' at " + i);
        }
    }

    private static string ParseString(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++; // opening quote
        while (true)
        {
            if (i >= s.Length) throw new JEx("unterminated string");
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c == '\\')
            {
                if (i >= s.Length) throw new JEx("bad escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        {
                            if (i + 4 > s.Length) throw new JEx("bad \\u");
                            int cp = 0;
                            for (int k = 0; k < 4; k++)
                            {
                                int d = Hex(s[i + k]);
                                if (d < 0) throw new JEx("bad \\u hex");
                                cp = cp * 16 + d;
                            }
                            i += 4;
                            sb.Append((char)cp);
                            break;
                        }
                    default: throw new JEx("bad escape \\" + e);
                }
                continue;
            }
            if (c < 0x20) throw new JEx("control char in string");
            sb.Append(c);
        }
    }

    private static JVal ParseNumber(string s, ref int i)
    {
        int st = i;
        if (i < s.Length && s[i] == '-') i++;
        if (i >= s.Length) throw new JEx("bad number");
        if (s[i] == '0') i++;
        else if (s[i] >= '1' && s[i] <= '9') { while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++; }
        else throw new JEx("bad number at " + st);
        bool isInt = true;
        if (i < s.Length && s[i] == '.')
        {
            isInt = false;
            i++;
            if (i >= s.Length || s[i] < '0' || s[i] > '9') throw new JEx("bad fraction");
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            isInt = false;
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            if (i >= s.Length || s[i] < '0' || s[i] > '9') throw new JEx("bad exponent");
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
        }
        string tok = s.Substring(st, i - st);
        if (isInt && long.TryParse(tok, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            return JNum.Of(l);
        if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            throw new JEx("bad number " + tok);
        return JNum.Of(d);
    }

    private static void Expect(string s, ref int i, string word)
    {
        if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
            throw new JEx("expected " + word + " at " + i);
        i += word.Length;
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
    }

    private static int Hex(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    /* ---------------- 序列化 ---------------- */

    /// <summary>对齐 org.json 的 JSONObject.quote()。</summary>
    public static string Quote(string str)
    {
        if (str == null) return "\"\"";
        var sb = new StringBuilder(str.Length + 2);
        sb.Append('"');
        foreach (char c in str)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ' || (c >= '\u0080' && c < '\u00a0') || (c >= '\u2000' && c < '\u2100'))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
