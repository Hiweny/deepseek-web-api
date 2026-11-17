package com.hiweny.dswebapi;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

/** 多账号的持久化与配置项（应用私有 SharedPreferences，Cookie 属敏感数据，仅本机保存）。 */
public final class AccountVault {
    private static final String PREF = "dswb_accounts";
    /** localStorage 快照单独存一个文件，避免每次 persist 主列表时重复写几 MB。 */
    private static final String PREF_LS = "dswb_accounts_ls";
    private static final String K_LIST = "list";
    private static final String K_ACTIVE = "active";
    private static final String K_ROTATE = "rotate";
    private static final String K_LIMIT = "limit";
    private static final String K_COOLDOWN = "cooldown";

    private AccountVault() {}

    public static SharedPreferences sp(Context c) {
        return c.getApplicationContext().getSharedPreferences(PREF, Context.MODE_PRIVATE);
    }

    public static List<AccountSlot> load(Context c) {
        List<AccountSlot> out = new ArrayList<>();
        try {
            String raw = sp(c).getString(K_LIST, "");
            if (raw != null && !raw.isEmpty()) {
                JSONArray arr = new JSONArray(raw);
                for (int i = 0; i < arr.length(); i++) {
                    JSONObject o = arr.optJSONObject(i);
                    if (o != null) {
                        AccountSlot s = AccountSlot.fromJson(o);
                        if (s.id == null || s.id.isEmpty()) s.id = newId();
                        out.add(s);
                    }
                }
            }
        } catch (Throwable t) {
            Util.log("读取账号列表失败: " + t.getMessage());
        }
        return out;
    }

    public static void save(Context c, List<AccountSlot> list) {
        try {
            JSONArray arr = new JSONArray();
            for (AccountSlot s : list) arr.put(s.toJson());
            sp(c).edit().putString(K_LIST, arr.toString()).apply();
        } catch (Throwable t) {
            Util.log("保存账号列表失败: " + t.getMessage());
        }
    }

    public static String activeId(Context c) { return sp(c).getString(K_ACTIVE, ""); }
    public static void setActiveId(Context c, String id) { sp(c).edit().putString(K_ACTIVE, id == null ? "" : id).apply(); }

    public static boolean rotateEnabled(Context c) { return sp(c).getBoolean(K_ROTATE, true); }
    public static void setRotateEnabled(Context c, boolean v) { sp(c).edit().putBoolean(K_ROTATE, v).apply(); }

    /** 同一账号在同一对话里最多连发几次。 */
    public static int sendLimit(Context c) {
        int v = sp(c).getInt(K_LIMIT, 3);
        return v < 1 ? 1 : (v > 50 ? 50 : v);
    }
    public static void setSendLimit(Context c, int v) { sp(c).edit().putInt(K_LIMIT, Math.max(1, Math.min(50, v))).apply(); }

    /** 命中「消息发送频繁」后的冷却分钟数。 */
    public static int cooldownMin(Context c) {
        int v = sp(c).getInt(K_COOLDOWN, 30);
        return v < 0 ? 0 : (v > 720 ? 720 : v);
    }
    public static void setCooldownMin(Context c, int v) { sp(c).edit().putInt(K_COOLDOWN, Math.max(0, Math.min(720, v))).apply(); }

    public static void saveStorage(Context c, String id, String b64) {
        if (id == null || id.isEmpty()) return;
        try {
            c.getApplicationContext().getSharedPreferences(PREF_LS, Context.MODE_PRIVATE)
                    .edit().putString(id, b64 == null ? "" : b64).apply();
        } catch (Throwable ignored) {}
    }

    public static String loadStorage(Context c, String id) {
        if (id == null || id.isEmpty()) return "";
        try {
            return c.getApplicationContext().getSharedPreferences(PREF_LS, Context.MODE_PRIVATE)
                    .getString(id, "");
        } catch (Throwable t) { return ""; }
    }

    public static void removeStorage(Context c, String id) {
        if (id == null || id.isEmpty()) return;
        try {
            c.getApplicationContext().getSharedPreferences(PREF_LS, Context.MODE_PRIVATE)
                    .edit().remove(id).apply();
        } catch (Throwable ignored) {}
    }

    public static String newId() { return UUID.randomUUID().toString().replace("-", "").substring(0, 10); }
}
