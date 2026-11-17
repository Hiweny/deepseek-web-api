package com.hiweny.dswebapi;

import android.content.Context;

import java.util.ArrayList;
import java.util.List;

/**
 * 账号池 + 严格顺序轮换（与桌面端 v1.0.13 的调度策略保持一致）：
 *
 *  1. 账号**严格按顺序** 1 → 2 → 3 → … → 回绕，不按用量/空闲时间打分；
 *  2. 同一个账号在**同一个对话里最多连发 N 次**（默认 2），发满就换下一个；
 *  3. 所有账号都轮完一遍 → 进入下一轮，**每个账号都先新开对话**（不在旧对话上续，避免串味/超上下文）；
 *  4. 某账号命中「消息发送频繁」→ 进入冷却（默认 30 分钟），期间跳过；
 *  5. 所有账号都在冷却 → acquire 返回 null，调用方回 429。
 */
public final class AccountPool {
    private static final AccountPool I = new AccountPool();
    public static AccountPool get() { return I; }

    private final Object lk = new Object();
    private final List<AccountSlot> slots = new ArrayList<>();
    private int cursor = 0;      // 下一棒
    private int cycleNo = 1;     // 当前第几轮
    private Context app;

    private AccountPool() {}

    /** 一次取号的结果：用哪个账号 + 用之前是否要先新开对话。 */
    public static class Pick {
        public final AccountSlot slot;
        public final boolean needNewChat;
        public final boolean allCooling;
        Pick(AccountSlot s, boolean n, boolean c) { slot = s; needNewChat = n; allCooling = c; }
    }

    public void ensureLoaded(Context c) {
        if (c != null) app = c.getApplicationContext();
        synchronized (lk) {
            if (!slots.isEmpty()) return;
            slots.addAll(AccountVault.load(app));
            for (AccountSlot s : slots) s.storage = AccountVault.loadStorage(app, s.id);
            cursor = 0;
            cycleNo = 1;
        }
        Util.log("账号池载入 " + size() + " 个账号");
    }

    public Context ctx() { return app; }

    public List<AccountSlot> snapshot() {
        synchronized (lk) { return new ArrayList<>(slots); }
    }

    public int size() { synchronized (lk) { return slots.size(); } }

    public AccountSlot find(String id) {
        if (id == null) return null;
        synchronized (lk) {
            for (AccountSlot s : slots) if (id.equals(s.id)) return s;
        }
        return null;
    }

    public AccountSlot add(String name) {
        AccountSlot s = new AccountSlot();
        s.id = AccountVault.newId();
        s.name = (name == null || name.trim().isEmpty()) ? ("账号 " + (size() + 1)) : name.trim();
        s.createdAt = System.currentTimeMillis();
        s.enabled = true;
        synchronized (lk) { slots.add(s); }
        persist();
        Util.log("新增账号槽 " + s.id + "（" + s.name + "）");
        return s;
    }

    public boolean remove(String id) {
        boolean ok = false;
        synchronized (lk) {
            for (int i = 0; i < slots.size(); i++) {
                if (slots.get(i).id.equals(id)) {
                    slots.remove(i);
                    if (i < cursor) cursor--;
                    if (cursor >= slots.size()) cursor = 0;
                    ok = true;
                    break;
                }
            }
        }
        if (ok) {
            if (app != null) AccountVault.removeStorage(app, id);
            persist();
            Util.log("删除账号槽 " + id);
        }
        return ok;
    }

    public void rename(String id, String name) {
        AccountSlot s = find(id);
        if (s != null && name != null && !name.trim().isEmpty()) { s.name = name.trim(); persist(); }
    }

    public void setEnabled(String id, boolean on) {
        AccountSlot s = find(id);
        if (s != null) { s.enabled = on; if (!on) s.loggedIn = false; persist(); }
    }

    public void persist() {
        if (app == null) return;
        AccountVault.save(app, snapshot());
    }

    public void clearForTest() {
        synchronized (lk) { slots.clear(); cursor = 0; cycleNo = 1; }
    }

    public void attachForTest(AccountSlot s) {
        if (s == null) return;
        synchronized (lk) {
            for (AccountSlot x : slots) if (x.id.equals(s.id)) return;
            slots.add(s);
        }
    }

    private int limit() {
        return app == null ? 2 : AccountVault.sendLimit(app);
    }

    private static boolean usable(AccountSlot s) {
        return s != null && s.enabled && s.loggedIn && !s.inCooldown();
    }

    /**
     * 取一个账号来发送。返回 null 表示没有可用账号（调用方应回 429/503）。
     * 名额在锁内**预占**（sentInSession++），并发请求因此会落到不同账号。
     */
    public Pick acquire() {
        int lim = limit();
        synchronized (lk) {
            int n = slots.size();
            if (n == 0) return null;
            if (cursor < 0 || cursor >= n) cursor = 0;

            boolean anyUsable = false;
            for (AccountSlot s : slots) if (usable(s)) { anyUsable = true; break; }
            if (!anyUsable) {
                boolean anyLogged = false;
                for (AccountSlot s : slots) if (s.enabled && s.loggedIn) { anyLogged = true; break; }
                return new Pick(null, false, anyLogged);   // null.slot + allCooling
            }

            // ① 续用当前这一棒：可用、还有余额、没有在途
            AccountSlot cur = slots.get(cursor);
            if (usable(cur) && cur.sentInSession < lim && cur.reserved == 0) {
                return reserveLocked(cur);
            }

            // ② 当前这一棒忙/在途 → 找别的「空闲且还有余额」的账号（并发分散）
            for (int k = 1; k < n; k++) {
                int idx = (cursor + k) % n;
                AccountSlot s2 = slots.get(idx);
                if (!usable(s2)) continue;
                if (s2.sentInSession >= lim) continue;
                if (s2.reserved > 0) continue;
                cursor = idx;
                return reserveLocked(s2);
            }

            // ③ 其它账号都不空闲，但当前账号还有余额 → 排队用它
            if (usable(cur) && cur.sentInSession < lim) return reserveLocked(cur);

            // ④ 本轮余额都耗尽 → 开新一轮（全员新开对话）
            startNewCycleLocked();
            for (int k = 0; k < n; k++) {
                AccountSlot s3 = slots.get(k);
                if (!usable(s3)) continue;
                cursor = k;
                return reserveLocked(s3);
            }
            return null;
        }
    }

    private Pick reserveLocked(AccountSlot s) {
        s.sentInSession++;
        s.reserved++;
        s.roundNo = cycleNo;
        boolean need = s.needNewChat;
        if (need) s.needNewChat = false;
        return new Pick(s, need, false);
    }

    private void startNewCycleLocked() {
        cycleNo++;
        cursor = 0;
        for (AccountSlot s : slots) {
            s.sentInSession = 0;
            s.reserved = 0;
            s.roundNo = cycleNo;
            s.needNewChat = true;
        }
        Util.log("账号池进入第 " + cycleNo + " 轮（全体新开对话）");
    }

    /** 归还名额并记账。rateLimited=true 时进入冷却并要求下次先新开对话。 */
    public void release(AccountSlot s, boolean ok, boolean rateLimited) {
        if (s == null) return;
        s.reserved = Math.max(0, s.reserved - 1);
        s.lastUsedMs = System.currentTimeMillis();
        s.totalCalls++;
        if (ok) s.okCalls++;
        if (rateLimited) {
            s.rateLimitedCount++;
            int min = app == null ? 30 : AccountVault.cooldownMin(app);
            s.cooldownUntil = System.currentTimeMillis() + min * 60_000L;
            s.sentInSession = 0;
            s.needNewChat = true;
            s.lastError = "消息发送频繁，冷却 " + min + " 分钟";
            Util.log("账号 " + s.name + " 命中限流，冷却 " + min + " 分钟");
        }
        persist();
    }

    /**
     * 预测「下一次 acquire 会选中谁」（不改变任何状态，仅用于空闲预切换）。
     */
    public AccountSlot predict() {
        int lim = limit();
        synchronized (lk) {
            int n = slots.size();
            if (n == 0) return null;
            if (cursor < 0 || cursor >= n) cursor = 0;
            AccountSlot cur = slots.get(cursor);
            if (usable(cur) && cur.sentInSession < lim && cur.reserved == 0) return cur;
            for (int k = 1; k < n; k++) {
                AccountSlot s = slots.get((cursor + k) % n);
                if (usable(s) && s.sentInSession < lim && s.reserved == 0) return s;
            }
            if (usable(cur) && cur.sentInSession < lim) return cur;
            for (int k = 0; k < n; k++) {
                AccountSlot s = slots.get(k);
                if (usable(s)) return s;
            }
            return null;
        }
    }

    /** 最短的冷却剩余秒数（全部冷却时用于 Retry-After）。 */
    public int minCooldownLeftSec() {
        int min = Integer.MAX_VALUE;
        synchronized (lk) {
            for (AccountSlot s : slots) {
                if (s.enabled && s.loggedIn && s.inCooldown()) min = Math.min(min, s.cooldownLeftSec());
            }
        }
        return min == Integer.MAX_VALUE ? 0 : min;
    }

    /** 当前轮次（给 UI 显示）。 */
    public int cycleNo() { synchronized (lk) { return cycleNo; } }
}
