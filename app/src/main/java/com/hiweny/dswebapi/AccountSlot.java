package com.hiweny.dswebapi;

import org.json.JSONObject;

/**
 * 一个 DeepSeek 账号槽。
 *
 * 安卓端无法像桌面端那样给每个账号一个独立的 WebView2 数据目录，
 * 因此这里用「Cookie 快照」表达一个账号：切换账号时把整份 Cookie 串替换掉，
 * 页面重载后就是另一个登录态。数美设备指纹 Cookie 一并随快照走，
 * 所以每个账号天然拥有各自的 device_id。
 */
public class AccountSlot {
    /* ---- 持久化字段 ---- */
    public String id = "";
    public String name = "";
    public boolean enabled = true;
    /** 该账号在 chat.deepseek.com 下的完整 Cookie header 原文（含 httpOnly，敏感，不打印） */
    public String cookies = "";
    public String deviceId = "";
    /** 该账号的 localStorage 快照（base64(JSON)），切换账号时一并还原，做到彻底隔离 */
    public String storage = "";
    public long createdAt = 0L;
    public long lastLoginAt = 0L;
    public long lastUsedMs = 0L;
    public int totalCalls = 0;
    public int okCalls = 0;
    public int rateLimitedCount = 0;
    public String lastError = "";

    /* ---- 运行态（不持久化） ---- */
    public volatile boolean loggedIn = false;
    public volatile int sentInSession = 0;
    public volatile int reserved = 0;
    public volatile boolean needNewChat = false;
    public volatile long cooldownUntil = 0L;
    public volatile int roundNo = 1;

    public boolean hasCookie() { return cookies != null && !cookies.isEmpty(); }

    public boolean inCooldown() { return cooldownUntil > System.currentTimeMillis(); }

    public int cooldownLeftSec() {
        long d = cooldownUntil - System.currentTimeMillis();
        return d <= 0 ? 0 : (int) Math.ceil(d / 1000.0);
    }

    /** 是否可参与调度。 */
    public boolean usable() { return enabled && loggedIn && !inCooldown(); }

    public String stateText() {
        if (!enabled) return "已停用";
        if (inCooldown()) return "冷却 " + fmtLeft(cooldownLeftSec());
        if (!loggedIn) return "未登录";
        return (needNewChat ? "待新对话" : "已发 " + sentInSession);
    }

    public static String fmtLeft(int sec) {
        if (sec <= 0) return "—";
        if (sec < 60) return sec + "s";
        if (sec < 3600) return (sec / 60) + "m";
        return (sec / 3600) + "h" + ((sec % 3600) / 60) + "m";
    }

    public JSONObject toJson() {
        JSONObject o = new JSONObject();
        try {
            o.put("id", id);
            o.put("name", name);
            o.put("enabled", enabled);
            o.put("cookies", cookies == null ? "" : cookies);
            o.put("device", deviceId == null ? "" : deviceId);
            o.put("created", createdAt);
            o.put("login", lastLoginAt);
            o.put("used", lastUsedMs);
            o.put("calls", totalCalls);
            o.put("ok", okCalls);
            o.put("rl", rateLimitedCount);
            o.put("err", lastError == null ? "" : lastError);
        } catch (Exception ignored) {}
        return o;
    }

    public static AccountSlot fromJson(JSONObject o) {
        AccountSlot s = new AccountSlot();
        if (o == null) return s;
        s.id = o.optString("id", "");
        s.name = o.optString("name", "账号");
        s.enabled = o.optBoolean("enabled", true);
        s.cookies = o.optString("cookies", "");
        s.deviceId = o.optString("device", "");
        s.createdAt = o.optLong("created", 0L);
        s.lastLoginAt = o.optLong("login", 0L);
        s.lastUsedMs = o.optLong("used", 0L);
        s.totalCalls = o.optInt("calls", 0);
        s.okCalls = o.optInt("ok", 0);
        s.rateLimitedCount = o.optInt("rl", 0);
        s.lastError = o.optString("err", "");
        s.loggedIn = s.enabled && s.hasCookie();   // 乐观初值，随后由 probe 校正
        return s;
    }
}
