package com.hiweny.dswebapi;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.view.ViewGroup;
import android.webkit.ConsoleMessage;
import android.webkit.CookieManager;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;

/**
 * 全局唯一的 WebView 宿主（由前台服务持有），保证：
 *  - 与 Activity 生命周期解耦：Activity 关闭/进程后台化后仍持续运行，开机后自动恢复；
 *  - 会话唯一：Activity 打开时把这个同一个 WebView 挂进界面，登录态与 API 驱动共用一份。
 */
public final class WebHost {
    public static final String DS_URL = "https://chat.deepseek.com/";
    private static final String DESKTOP_UA =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static volatile WebView sWebView;
    private static volatile String sBridgeJs = "";
    private static volatile boolean sDark = false;
    private static volatile boolean sPageLoaded = false;
    private static volatile Activity sActivity;
    private static volatile FileChooserHandler sFileChooser;
    private static final Handler MAIN = new Handler(Looper.getMainLooper());

    private WebHost() {}

    public interface FileChooserHandler {
        boolean onShow(ValueCallback<Uri[]> cb, WebChromeClient.FileChooserParams params);
    }

    public static WebView get() { return sWebView; }
    public static boolean isReady() { return sWebView != null && sPageLoaded; }
    public static void setActivity(Activity a) { sActivity = a; }
    public static void setFileChooser(FileChooserHandler h) { sFileChooser = h; }

    public static void runOnMain(Runnable r) {
        if (Looper.myLooper() == Looper.getMainLooper()) r.run();
        else MAIN.post(r);
    }

    /** 确保 WebView 已创建并加载官网（幂等）。 */
    public static void ensure(final Context appCtx, final String bridgeJs) {
        if (bridgeJs != null && !bridgeJs.isEmpty()) sBridgeJs = bridgeJs;
        if (sWebView != null) return;
        runOnMain(() -> {
            if (sWebView != null) return;
            try {
                WebView wv = new WebView(appCtx.getApplicationContext());
                configure(wv);
                sWebView = wv;
                DeepSeekController.get().attach(wv, sBridgeJs, ApiService::onBridgeStatus);
                wv.loadUrl(DS_URL);
                Util.log("WebView 已创建并加载 " + DS_URL);
            } catch (Throwable t) {
                Util.log("WebView 创建失败: " + t.getMessage());
            }
        });
    }

    public static void setDark(boolean dark) {
        sDark = dark;
        final WebView wv = sWebView;
        if (wv == null) return;
        runOnMain(() -> {
            try {
                WebSettings s = wv.getSettings();
                if (Build.VERSION.SDK_INT >= 33) s.setAlgorithmicDarkeningAllowed(dark);
                else if (Build.VERSION.SDK_INT >= 29) s.setForceDark(dark ? WebSettings.FORCE_DARK_ON : WebSettings.FORCE_DARK_OFF);
            } catch (Throwable ignored) {}
            DeepSeekController.get().setTheme(dark);
        });
    }

    /** 把 WebView 挂进某个容器（Activity 打开时）。 */
    public static void attachTo(ViewGroup parent) {
        final WebView wv = sWebView;
        if (wv == null || parent == null) return;
        runOnMain(() -> {
            try {
                ViewGroup old = (ViewGroup) wv.getParent();
                if (old != null && old != parent) old.removeView(wv);
                if (wv.getParent() == null) parent.addView(wv, new ViewGroup.LayoutParams(-1, -1));
            } catch (Throwable t) { Util.log("挂载 WebView 失败: " + t.getMessage()); }
        });
    }

    /** 从容器摘下（Activity 关闭时），但保留实例继续后台运行。 */
    public static void detachFromParent() {
        final WebView wv = sWebView;
        if (wv == null) return;
        runOnMain(() -> {
            try {
                ViewGroup p = (ViewGroup) wv.getParent();
                if (p != null) p.removeView(wv);
            } catch (Throwable ignored) {}
        });
    }

    public static void reload() {
        final WebView wv = sWebView;
        if (wv != null) runOnMain(() -> { try { wv.reload(); } catch (Throwable ignored) {} });
    }

    public static void injectBridge() { DeepSeekController.get().injectBridge(); }

    /* ================= 内部 ================= */

    private static void configure(WebView wv) {
        try {
            if (Build.VERSION.SDK_INT >= 26) {
                WebView.setRendererPriorityPolicy(WebView.RENDERER_PRIORITY_IMPORTANT, false);
            }
        } catch (Throwable ignored) {}
        WebSettings s = wv.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setDatabaseEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setMixedContentMode(WebSettings.MIXED_CONTENT_COMPATIBILITY_MODE);
        s.setLoadWithOverviewMode(true);
        s.setUseWideViewPort(true);
        s.setTextZoom(100);
        s.setSupportZoom(true);
        s.setBuiltInZoomControls(true);
        s.setDisplayZoomControls(false);
        s.setUserAgentString(DESKTOP_UA);
        s.setJavaScriptCanOpenWindowsAutomatically(true);
        s.setAllowFileAccess(true);
        applyDark(wv);
        CookieManager cm = CookieManager.getInstance();
        cm.setAcceptCookie(true);
        cm.setAcceptThirdPartyCookies(wv, true);
        wv.setBackgroundColor(sDark ? 0xFF0E1116 : 0xFFFFFFFF);
        wv.addJavascriptInterface(new JsBridge(), "DSB");
        wv.setWebViewClient(new WebViewClient() {
            @Override public void onPageFinished(WebView view, String url) {
                super.onPageFinished(view, url);
                Util.log("页面加载完成: " + url);
                sPageLoaded = true;
                DeepSeekController.get().injectBridge();
                MAIN.postDelayed(() -> DeepSeekController.get().probe(), 1000L);
            }
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest req) {
                String u = req.getUrl().toString();
                return !(u.startsWith("http://") || u.startsWith("https://"));
            }
        });
        wv.setWebChromeClient(new WebChromeClient() {
            @Override public boolean onShowFileChooser(WebView webView, ValueCallback<Uri[]> cb, FileChooserParams params) {
                FileChooserHandler h = sFileChooser;
                if (h != null) { try { return h.onShow(cb, params); } catch (Throwable ignored) {} }
                try { cb.onReceiveValue(null); } catch (Throwable ignored) {}
                return false;
            }
            @Override public boolean onConsoleMessage(ConsoleMessage m) {
                if (m != null && m.message() != null && m.message().contains("DSWB")) Util.log("JS: " + m.message());
                return true;
            }
        });
    }

    private static void applyDark(WebView wv) {
        try {
            WebSettings s = wv.getSettings();
            if (Build.VERSION.SDK_INT >= 33) s.setAlgorithmicDarkeningAllowed(sDark);
            else if (Build.VERSION.SDK_INT >= 29) s.setForceDark(sDark ? WebSettings.FORCE_DARK_ON : WebSettings.FORCE_DARK_OFF);
        } catch (Throwable ignored) {}
    }

    public static String loadBridgeJs(Context ctx) {
        try (InputStream in = ctx.getAssets().open("bridge.js");
             ByteArrayOutputStream bos = new ByteArrayOutputStream()) {
            byte[] buf = new byte[8192];
            int n;
            while ((n = in.read(buf)) > 0) bos.write(buf, 0, n);
            return bos.toString("UTF-8");
        } catch (Exception e) {
            Util.log("加载 bridge.js 失败: " + e.getMessage());
            return "";
        }
    }

    private static class JsBridge {
        @android.webkit.JavascriptInterface
        public void onEvent(String json) { DeepSeekController.get().onJsEvent(json); }
    }
}
