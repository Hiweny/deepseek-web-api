package com.hiweny.dswebapi;

import android.webkit.CookieManager;
import android.webkit.WebView;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * 账号切换器：把「一个账号」实现为一份 Cookie 快照。
 *
 * 切换流程（全部在主线程执行 CookieManager 操作，其他线程用 latch 等待）：
 *   1. 把当前登录态快照回当前账号；
 *   2. removeAllCookies → 逐条写入目标账号的 Cookie；
 *   3. 清空 localStorage / sessionStorage（避免旧账号的缓存串味）；
 *   4. 重新加载 chat.deepseek.com，等待 probe 报「已登录」。
 */
public final class AccountSwitcher {
    public static final String URL = "https://chat.deepseek.com/";
    private static final long COOKIE_WAIT_MS = 5000L;
    private static final long STORAGE_WAIT_MS = 2000L;

    private AccountSwitcher() {}

    /** 读取当前 WebView 在 chat.deepseek.com 下的完整 Cookie 串（含 httpOnly）。 */
    public static String readCookies() {
        final String[] out = {""};
        final CountDownLatch l = new CountDownLatch(1);
        WebHost.runOnMain(() -> {
            try {
                CookieManager cm = CookieManager.getInstance();
                cm.setAcceptCookie(true);
                String c = cm.getCookie(URL);
                out[0] = c == null ? "" : c;
            } catch (Throwable t) {
                Util.log("读取 Cookie 失败: " + t.getMessage());
            } finally {
                l.countDown();
            }
        });
        try { l.await(3, TimeUnit.SECONDS); } catch (InterruptedException ignored) {}
        return out[0];
    }

    /** 把当前登录态写回账号槽（不打印 Cookie 内容）。 */
    public static void snapshot(AccountSlot s) {
        if (s == null) return;
        String c = readCookies();
        if (!c.isEmpty()) {
            s.cookies = c;
            s.lastLoginAt = System.currentTimeMillis();
            s.loggedIn = true;
        }
    }

    /** 用目标账号的 Cookie 覆盖当前浏览器登录态（空串 = 全清）。 */
    public static void applyCookies(String cookies, long waitMs) {
        final String kvAll = cookies == null ? "" : cookies;
        final CountDownLatch l = new CountDownLatch(1);
        WebHost.runOnMain(() -> {
            final CookieManager cm = CookieManager.getInstance();
            try {
                cm.setAcceptCookie(true);
                cm.removeAllCookies(v -> {
                    try {
                        if (!kvAll.isEmpty()) {
                            String[] parts = kvAll.split(";");
                            for (String p : parts) {
                                String t = p.trim();
                                if (t.isEmpty()) continue;
                                int eq = t.indexOf('=');
                                if (eq <= 0) continue;
                                cm.setCookie(URL, t + "; Path=/; Secure");
                            }
                        }
                        cm.flush();
                    } catch (Throwable e) {
                        Util.log("写入 Cookie 失败: " + e.getMessage());
                    } finally {
                        l.countDown();
                    }
                });
            } catch (Throwable t) {
                Util.log("清空 Cookie 失败: " + t.getMessage());
                l.countDown();
            }
        });
        try { l.await(waitMs, TimeUnit.MILLISECONDS); } catch (InterruptedException ignored) {}
    }

    /** 清空当前页面的 localStorage / sessionStorage。 */
    public static void clearStorage(long waitMs) {
        final CountDownLatch l = new CountDownLatch(1);
        WebHost.runOnMain(() -> {
            WebView wv = WebHost.get();
            if (wv == null) { l.countDown(); return; }
            try {
                wv.evaluateJavascript(
                        "try{localStorage.clear();sessionStorage.clear();}catch(e){}",
                        v -> l.countDown());
            } catch (Throwable t) {
                l.countDown();
            }
        });
        try { l.await(waitMs, TimeUnit.MILLISECONDS); } catch (InterruptedException ignored) {}
    }

    /** 等待页面重新加载并 probe 出「已登录」。 */
    public static boolean awaitReady(long timeoutMs) {
        long end = System.currentTimeMillis() + Math.max(1000L, timeoutMs);
        while (System.currentTimeMillis() < end) {
            if (WebHost.isReady() && ApiService.probeLoggedIn()) return true;
            if (Thread.currentThread().isInterrupted()) return false;
            try { Thread.sleep(200L); } catch (InterruptedException e) { return false; }
        }
        return WebHost.isReady() && ApiService.probeLoggedIn();
    }

    /**
     * 切换到目标账号。current 为当前正在用的账号（可为 null），用于先做快照。
     * 返回 true 表示页面已就绪且登录态有效。
     */
    public static boolean switchTo(AccountSlot target, AccountSlot current, long timeoutMs) {
        if (target == null) return false;
        if (current != null && current != target) snapshot(current);

        if (!target.hasCookie()) {
            // 尚未绑定登录态的账号：清空后让用户手动登录
            prepareForLogin();
            return true;
        }
        applyCookies(target.cookies, COOKIE_WAIT_MS);
        clearStorage(STORAGE_WAIT_MS);
        ApiService.resetProbe();
        WebHost.loadUrl(URL);
        boolean ok = awaitReady(timeoutMs);
        target.loggedIn = ok;
        if (ok) target.lastLoginAt = System.currentTimeMillis();
        Util.log("切换到账号 " + target.name + " → " + (ok ? "已登录" : "未登录/超时"));
        return ok;
    }

    /** 准备手动登录一个新账号：清空所有登录痕迹并回到登录页。 */
    public static void prepareForLogin() {
        applyCookies("", COOKIE_WAIT_MS);
        clearStorage(STORAGE_WAIT_MS);
        ApiService.resetProbe();
        WebHost.loadUrl(URL);
    }

    /** 把当前页面登录态绑定到账号槽，并尽量读一次数美设备号（用于展示/排查）。 */
    public static void bindCurrent(AccountSlot s) {
        if (s == null) return;
        snapshot(s);
        final CountDownLatch l = new CountDownLatch(1);
        WebHost.runOnMain(() -> {
            WebView wv = WebHost.get();
            if (wv == null) { l.countDown(); return; }
            try {
                wv.evaluateJavascript(
                        "(function(){try{return (window.SMSdk&&window.SMSdk.getDeviceId)?window.SMSdk.getDeviceId():''}catch(e){return ''}})()",
                        v -> {
                            try {
                                if (v != null && v.length() > 2 && !"null".equals(v)) {
                                    String d = v.replace("\"", "");
                                    if (!d.isEmpty()) s.deviceId = d.length() > 12 ? d.substring(0, 12) : d;
                                }
                            } catch (Throwable ignored) {}
                            l.countDown();
                        });
            } catch (Throwable t) {
                l.countDown();
            }
        });
        try { l.await(1500, TimeUnit.MILLISECONDS); } catch (InterruptedException ignored) {}
    }
}
