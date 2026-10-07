package com.hiweny.dswebapi;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/**
 * 工具调用参数「按声明类型」归一化（与桌面端 Core/ToolArgsFixer.cs 同源）。
 *
 * <p>背景：客户端（宿主 / MCP 框架 / Agent 运行时）拿到 tool_calls 之后，会对某些参数的值
 * 「再解析一次 JSON」——例如 package_proxy 的 params、browser 的 options。
 * 如果模型把本该是字符串的参数写成了对象/数组，宿主就会解析失败，
 * 典型报错：Exactly one params parameter is required。
 *
 * <p>规则（严格按工具定义里的 JSON Schema 判断，不猜字段名）：
 * <ol>
 *   <li>声明为 string（且未同时声明 object/array）的参数，若模型给了对象/数组
 *       → 序列化成「只转义一层」的 JSON 字符串；</li>
 *   <li>声明为 object / array 的参数一律不动（保持对象/数组）；</li>
 *   <li>没有 schema、或该参数没有类型声明时一律不动（保守，绝不引入新问题）。</li>
 * </ol>
 *
 * <p>注意：只做「对象 → 字符串」这一个方向。反向（字符串 → 对象）不做，
 * 因为对声明为 string 的字段做反向改写会破坏客户端「再解析一次」的约定。
 */
public final class ToolArgsFixer {

    private ToolArgsFixer() {
    }

    /** 按 schema 修正每个 tool_call 的 arguments；返回修正的字段个数。 */
    public static int fix(JSONArray calls, JSONArray tools) {
        int fixedCount = 0;
        if (calls == null || tools == null || tools.length() == 0) return 0;
        Map<String, Map<String, String>> index = buildIndex(tools);
        if (index.isEmpty()) return 0;

        for (int i = 0; i < calls.length(); i++) {
            JSONObject call = calls.optJSONObject(i);
            if (call == null) continue;
            String name = call.optString("name", "").trim();
            if (name.isEmpty()) continue;
            Map<String, String> props = index.get(name);
            if (props == null || props.isEmpty()) continue;

            JSONObject args = call.optJSONObject("arguments");   // 只在「对象形态」下逐字段修
            if (args == null) continue;
            List<String> keys = new ArrayList<>();
            for (Iterator<String> it = args.keys(); it.hasNext(); ) keys.add(it.next());
            for (String k : keys) {
                Object v = args.opt(k);
                if (v == null || v instanceof String) continue;                 // 已经是字符串 → 不动
                if (!(v instanceof JSONObject) && !(v instanceof JSONArray)) continue;  // 只处理对象/数组
                if (!"string".equals(props.get(k))) continue;                   // 声明必须是 string
                try {
                    args.put(k, v.toString());                                  // ★ 只转义一层的字符串
                    fixedCount++;
                    Util.log("工具参数归一化: " + name + "." + k + " 声明为 string，已把对象/数组序列化为字符串");
                } catch (Exception ignored) {
                }
            }
        }
        return fixedCount;
    }

    /** 从 OpenAI tools 数组建索引：工具名 → (参数名 → "string"/"other"/"")。 */
    private static Map<String, Map<String, String>> buildIndex(JSONArray tools) {
        Map<String, Map<String, String>> idx = new HashMap<>();
        if (tools == null) return idx;
        for (int i = 0; i < tools.length(); i++) {
            JSONObject t = tools.optJSONObject(i);
            if (t == null) continue;
            JSONObject fn = t.optJSONObject("function");
            if (fn == null) fn = t;                       // 兼容 {function:{...}} 与扁平写法
            String name = fn.optString("name", "").trim();
            if (name.isEmpty()) continue;

            Map<String, String> props = new HashMap<>();
            JSONObject prms = asObject(fn.opt("parameters"));
            JSONObject properties = prms == null ? null : prms.optJSONObject("properties");
            if (properties != null) {
                for (Iterator<String> it = properties.keys(); it.hasNext(); ) {
                    String k = it.next();
                    Set<String> types = new HashSet<>();
                    collectTypes(properties.opt(k), types);
                    props.put(k, decide(types));
                }
            }
            idx.put(name, props);
        }
        return idx;
    }

    /** parameters 可能是对象，也可能是「字符串化的 JSON」。 */
    private static JSONObject asObject(Object o) {
        if (o instanceof JSONObject) return (JSONObject) o;
        if (o instanceof String) {
            String s = ((String) o).trim();
            if (s.isEmpty()) return null;
            try {
                return new JSONObject(s);
            } catch (Exception ignored) {
            }
            Object v = LooseJson.parse(s);
            if (v instanceof JSONObject) return (JSONObject) v;
        }
        return null;
    }

    /** 收集字段声明的类型（支持 type / anyOf / oneOf / allOf 与 type 数组）。 */
    private static void collectTypes(Object node, Set<String> out) {
        if (node instanceof JSONArray) {
            JSONArray a = (JSONArray) node;
            for (int i = 0; i < a.length(); i++) {
                Object x = a.opt(i);
                if (x instanceof String) out.add(((String) x).trim().toLowerCase(Locale.ROOT));
            }
            return;
        }
        if (!(node instanceof JSONObject)) return;
        JSONObject o = (JSONObject) node;
        Object tp = o.opt("type");
        if (tp instanceof String) {
            String s = ((String) tp).trim();
            if (!s.isEmpty()) out.add(s.toLowerCase(Locale.ROOT));
        } else if (tp instanceof JSONArray) {
            JSONArray ta = (JSONArray) tp;
            for (int i = 0; i < ta.length(); i++) {
                Object x = ta.opt(i);
                if (x instanceof String) out.add(((String) x).trim().toLowerCase(Locale.ROOT));
            }
        }
        for (String key : new String[]{"anyOf", "oneOf", "allOf"}) {
            JSONArray arr = o.optJSONArray(key);
            if (arr == null) continue;
            for (int i = 0; i < arr.length(); i++) collectTypes(arr.opt(i), out);
        }
    }

    /** 由类型集合决定处理方式：只有「明确是 string 且不含 object/array」才允许转换。 */
    private static String decide(Set<String> types) {
        if (types.isEmpty()) return "";                    // 没声明类型 → 不动
        if (types.contains("object") || types.contains("array")) return "other";
        if (types.contains("string")) return "string";
        return "other";
    }
}
