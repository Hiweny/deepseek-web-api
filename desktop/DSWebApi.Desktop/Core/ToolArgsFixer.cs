namespace DSWebApi.Desktop.Core;

/// <summary>
/// 工具调用参数「按声明类型」归一化。
///
/// 背景：客户端（宿主 / MCP 框架 / Agent 运行时）拿到 tool_calls 之后，会对某些参数的值
/// 「再解析一次 JSON」——例如 package_proxy 的 params、browser 的 options。
/// 如果模型把本该是字符串的参数写成了对象/数组，宿主就会解析失败，
/// 典型报错：Exactly one params parameter is required。
///
/// 规则（严格按工具定义里的 JSON Schema 判断，不猜字段名）：
///   1. 声明为 string（且未同时声明 object/array）的参数，若模型给了对象/数组
///      → 序列化成「只转义一层」的 JSON 字符串；
///   2. 声明为 object / array 的参数一律不动（保持对象/数组）；
///   3. 没有 schema、或该参数没有类型声明时一律不动（保守，绝不引入新问题）。
///
/// 注意：这里只做「对象 → 字符串」这一个方向。反向（字符串 → 对象）不做，
/// 因为对声明为 string 的字段做反向改写会破坏客户端「再解析一次」的约定。
/// </summary>
public static class ToolArgsFixer
{
    /// <summary>单个工具的参数类型表（参数名 → "string" / "other" / ""）。</summary>
    public sealed class Schema
    {
        public readonly Dictionary<string, string> PropType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>是否拿到了该工具的参数声明。</summary>
        public bool Known;
    }

    /// <summary>从 OpenAI tools 数组建索引：工具名 → 参数类型表。</summary>
    public static Dictionary<string, Schema> BuildIndex(JArr tools)
    {
        var idx = new Dictionary<string, Schema>(StringComparer.OrdinalIgnoreCase);
        if (tools == null) return idx;
        foreach (var it in tools.Items)
        {
            var t = it as JObj;
            if (t == null) continue;
            var fn = t.Obj("function") ?? t;                 // 兼容 {function:{...}} 与扁平写法
            string name = fn.Str("name", "").Trim();
            if (name.Length == 0) continue;

            var sc = new Schema();
            JObj prms = null;
            try { prms = Json.TryParse(fn.Str("parameters", "")) as JObj; } catch { }
            if (prms == null) prms = fn.Obj("parameters");

            var props = prms?.Obj("properties");
            if (props != null)
            {
                sc.Known = true;
                foreach (string k in props.Keys)
                {
                    var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CollectTypes(props.Get(k), types);
                    sc.PropType[k] = Decide(types);
                }
            }
            idx[name] = sc;
        }
        return idx;
    }

    /// <summary>按 schema 修正每个 tool_call 的 arguments；返回修正的字段个数。</summary>
    public static int Fix(JArr calls, Dictionary<string, Schema> index)
    {
        int fixedCount = 0;
        if (calls == null || index == null || index.Count == 0) return 0;
        foreach (var c in calls.Items)
        {
            var call = c as JObj;
            if (call == null) continue;
            string name = call.Str("name", "").Trim();
            if (name.Length == 0) continue;
            if (!index.TryGetValue(name, out var sc) || !sc.Known) continue;

            var args = call.Get("arguments") as JObj;        // 只在「对象形态」下逐字段修
            if (args == null) continue;
            foreach (string k in args.Keys.ToList())
            {
                var v = args.Get(k);
                if (v == null || v is JStr) continue;                    // 已经是字符串 → 不动
                if (!(v is JObj) && !(v is JArr)) continue;               // 只处理对象/数组
                if (!sc.PropType.TryGetValue(k, out var ty) || ty != "string") continue;  // 声明必须是 string
                args.Set(k, new JStr(v.ToJson()));                        // ★ 序列化成「只转义一层」的字符串
                fixedCount++;
                Log.Write("工具参数归一化: " + name + "." + k + " 声明为 string，已把对象/数组序列化为字符串");
            }
        }
        return fixedCount;
    }

    /// <summary>收集字段声明的类型（支持 type / anyOf / oneOf / allOf 与 type 数组）。</summary>
    private static void CollectTypes(JVal node, HashSet<string> outTypes)
    {
        var o = node as JObj;
        if (o == null)
        {
            var arr1 = node as JArr;                                  // 直接就是 ["string","null"]
            if (arr1 != null)
                foreach (var x in arr1.Items)
                    if (x is JStr s1) outTypes.Add(s1.V.Trim().ToLowerInvariant());
            return;
        }
        var tp = o.Get("type");
        if (tp is JStr ts && ts.V.Trim().Length > 0) outTypes.Add(ts.V.Trim().ToLowerInvariant());
        else if (tp is JArr ta)
            foreach (var x in ta.Items)
                if (x is JStr xs) outTypes.Add(xs.V.Trim().ToLowerInvariant());
        foreach (string key in new[] { "anyOf", "oneOf", "allOf" })
        {
            var arr = o.Arr(key);
            if (arr == null) continue;
            foreach (var x in arr.Items) CollectTypes(x, outTypes);
        }
    }

    /// <summary>由类型集合决定处理方式：只有「明确是 string 且不含 object/array」才允许转换。</summary>
    private static string Decide(HashSet<string> types)
    {
        if (types.Count == 0) return "";                              // 没声明类型 → 不动
        if (types.Contains("object") || types.Contains("array")) return "other";
        if (types.Contains("string")) return "string";
        return "other";
    }
}
