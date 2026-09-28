package com.hiweny.dswebapi;

import android.os.Handler;
import android.os.Looper;
import android.webkit.WebView;

import org.json.JSONObject;

import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/** 原生侧与注入脚本 bridge.js 的唯一通道：在主线程 eval JS，用 reqId/latch 同步取回结果，并支持流式回调。 */
public class DeepSeekController {
    private static volatile DeepSeekController sInstance;
    private volatile WebView webView;
    private volatile String bridgeJs = "";
    private volatile StatusListener listener;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Map<String, Pending> pending = new ConcurrentHashMap<>();
    private final Map<String, StreamListener> streams = new ConcurrentHashMap<>();

    public interface StatusListener { void onStatus(String json); }

    /** 流式回调：content/thinking 为累计全文，原生侧自行做增量差分。 */
    public interface StreamListener {
        void onDelta(String thinking, String content, boolean recalled);
        void onFinish(JSONObject result);
    }

    private static class Pending {
        final CountDownLatch latch = new CountDownLatch(1);
        volatile JSONObject result;
    }

    private DeepSeekController() {}

    public static DeepSeekController get() {
        if (sInstance == null) {
            synchronized (DeepSeekController.class) {
                if (sInstance == null) sInstance = new DeepSeekController();
            }
        }
        return sInstance;
    }

    public void attach(WebView view, String bridgeJs, StatusListener listener) {
        this.webView = view;
        this.bridgeJs = bridgeJs;
        this.listener = listener;
    }

    public void detach(WebView view) {
        if (this.webView == view) this.webView = null;
        failAll("WEBVIEW_DETACHED");
    }

    public boolean isAttached() { return webView != null; }
    public boolean isBusy() { return !pending.isEmpty() || !streams.isEmpty(); }

    private void failAll(String err) {
        for (Pending p : pending.values()) {
            p.result = err(err);
            p.latch.countDown();
        }
        pending.clear();
        for (Map.Entry<String, StreamListener> e : streams.entrySet()) {
            try { e.getValue().onFinish(err(err)); } catch (Exception ignored) {}
        }
        streams.clear();
    }

    public void injectBridge() { eval(bridgeJs); }

    private void eval(final String js) {
        if (js == null || js.isEmpty()) return;
        final WebView view = webView;
        if (view == null) return;
        main.post(() -> {
            try { view.evaluateJavascript(js, null); }
            catch (Exception e) { Util.log("eval失败: " + e.getMessage()); }
        });
    }

    public void probe() { eval("window.DSKB && DSKB.probe();"); }

    public void configure(String thinking, String search) {
        JSONObject o = new JSONObject();
        try { o.put("thinking", thinking); o.put("search", search); } catch (Exception ignored) {}
        eval("window.DSKB && DSKB.configure(" + o + ");");
    }

    public void setTheme(boolean dark) { eval("window.DSKB && DSKB.setTheme(" + dark + ");"); }

    public void onJsEvent(String json) {
        try {
            JSONObject o = new JSONObject(json);
            String type = o.optString("type");
            String reqId = o.optString("reqId", "");

            if ("delta".equals(type)) {
                StreamListener sl = reqId.isEmpty() ? null : streams.get(reqId);
                if (sl != null) {
                    try { sl.onDelta(o.optString("thinking"), o.optString("content"), o.optBoolean("recalled")); } catch (Exception ignored) {}
                }
                return;
            }

            // 需要精确关联的完成类事件
            Pending p = reqId.isEmpty() ? null : pending.remove(reqId);
            StreamListener sl = reqId.isEmpty() ? null : streams.remove(reqId);
            if (p != null) { p.result = o; p.latch.countDown(); }
            if (sl != null && ("reply".equals(type))) { try { sl.onFinish(o); } catch (Exception ignored) {} }

            if ("reply".equals(type)) {
                Util.log("回复 via=" + o.optString("via", "dom") + " ok=" + o.optBoolean("ok")
                        + (o.optBoolean("recalled") ? " [撤回已拦截]" : "")
                        + " 正文=" + o.optString("content").length() + "字 思考=" + o.optString("thinking").length() + "字"
                        + (o.optString("error").isEmpty() ? "" : " err=" + o.optString("error")));
                return;
            }
            if ("attach".equals(type)) {
                Util.log("附件挂载 " + (o.optBoolean("ok") ? "成功" : "失败") + " " + o.optString("name", "")
                        + (o.optBoolean("ok") ? "" : " err=" + o.optString("error")));
                return;
            }
            if ("newChat".equals(type)) { Util.log("新建对话: " + (o.optBoolean("ok") ? "成功" : "失败")); return; }
            if ("pageReply".equals(type)) {
                Util.log("页面手动回复 正文=" + o.optString("content").length() + "字"
                        + (o.optBoolean("recalled") ? " [撤回已拦截]" : ""));
                return;
            }
            if (p == null) forward(o);
        } catch (Exception e) {
            Util.log("onJsEvent 解析失败: " + e.getMessage());
        }
    }

    private void forward(final JSONObject o) {
        final StatusListener l = listener;
        if (l != null) main.post(() -> l.onStatus(o.toString()));
    }

    private static JSONObject err(String msg) {
        JSONObject o = new JSONObject();
        try { o.put("ok", false); o.put("error", msg); } catch (Exception ignored) {}
        return o;
    }

    private String newReqId() {
        return UUID.randomUUID().toString().replace("-", "").substring(0, 16);
    }

    /** 同步调用 bridge.js 的某个方法并等待结果。 */
    private JSONObject callJs(String method, JSONObject arg, int timeoutSec) {
        if (webView == null) return err("APP_NOT_RUNNING:请保持本 App 运行且已登录 DeepSeek 官网");
        try {
            String reqId = newReqId();
            arg.put("reqId", reqId);
            Pending p = new Pending();
            pending.put(reqId, p);
            eval("window.DSKB ? DSKB." + method + "(" + arg + ") : (function(){try{DSB.onEvent(JSON.stringify({type:'"
                    + method + "',reqId:'" + reqId + "',ok:false,error:'NO_BRIDGE'}))}catch(e){}})();");
            if (p.latch.await(timeoutSec, TimeUnit.SECONDS)) {
                return p.result == null ? err("NULL") : p.result;
            }
            pending.remove(reqId);
            return err("TIMEOUT");
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return err("INTERRUPTED");
        } catch (Exception e) {
            return err(e.getMessage());
        }
    }

    /** 非流式发送，阻塞等待完整回复。 */
    public JSONObject sendPrompt(String prompt, String sessionId, int timeoutSec) {
        JSONObject arg = new JSONObject();
        try {
            arg.put("prompt", prompt);
            if (sessionId != null && !sessionId.isEmpty()) arg.put("sessionId", sessionId);
        } catch (Exception ignored) {}
        return callJs("send", arg, timeoutSec);
    }

    /** 流式发送：立即返回 reqId，事件通过 listener 异步回传。 */
    public String sendPromptStream(String prompt, String sessionId, final int timeoutSec, final StreamListener l) {
        if (webView == null) {
            JSONObject e = err("APP_NOT_RUNNING:请保持本 App 运行且已登录 DeepSeek 官网");
            if (l != null) l.onFinish(e);
            return null;
        }
        final String reqId = newReqId();
        streams.put(reqId, new StreamListener() {
            @Override public void onDelta(String t, String c, boolean r) {
                if (l != null) l.onDelta(t, c, r);
            }
            @Override public void onFinish(JSONObject result) {
                main.removeCallbacksAndMessages(reqId);
                if (l != null) l.onFinish(result);
            }
        });
        JSONObject arg = new JSONObject();
        try {
            arg.put("prompt", prompt);
            arg.put("reqId", reqId);
            if (sessionId != null && !sessionId.isEmpty()) arg.put("sessionId", sessionId);
        } catch (Exception ignored) {}
        eval("window.DSKB ? DSKB.send(" + arg + ") : (function(){try{DSB.onEvent(JSON.stringify({type:'reply',reqId:'"
                + reqId + "',ok:false,error:'NO_BRIDGE'}))}catch(e){}})();");

        main.postDelayed(new Runnable() {
            @Override public void run() {
                if (streams.remove(reqId) != null) {
                    JSONObject e = err("TIMEOUT");
                    if (l != null) l.onFinish(e);
                }
            }
        }, (timeoutSec + 5) * 1000L);
        return reqId;
    }

    /** 上传附件（base64）到官网输入区，阻塞等待上传/解析完成。 */
    public JSONObject attachFile(String name, String mime, String base64, int timeoutSec) {
        JSONObject arg = new JSONObject();
        try { arg.put("name", name); arg.put("mime", mime); arg.put("b64", base64); } catch (Exception ignored) {}
        return callJs("attachFile", arg, timeoutSec);
    }

    public JSONObject newChat() { return callJs("newChat", new JSONObject(), 20); }
}
