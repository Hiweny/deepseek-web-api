package com.hiweny.dswebapi;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.view.WindowManager;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

/** 前台常驻服务：承载本地 OpenAI 兼容 HTTP 接口，把请求桥接到官网 WebView。 */
public class ApiService extends Service {

    public static final String ACTION_START = "com.hiweny.dswebapi.START";
    public static final String ACTION_STOP = "com.hiweny.dswebapi.STOP";
    private static final int NOTIF_ID = 1001;
    private static final String CHANNEL_ID = "dswebapi";

    public static volatile boolean serviceRunning = false;
    private static volatile ApiService sInstance;
    /** 供界面（悬浮球开关等）访问当前服务实例；未运行时为 null。 */
    public static ApiService instance() { return sInstance; }
    public static volatile long lastPingOk = 0L;
    private static volatile String sState = "未启动";

    private HttpBridgeServer server;
    private PowerManager.WakeLock wakeLock;
    private final ExecutorService chatExec = Executors.newSingleThreadExecutor();
    private final ScheduledExecutorService pingExec = Executors.newScheduledThreadPool(2);
    private final Handler watchdog = new Handler(Looper.getMainLooper());
    private static final AtomicInteger inflight = new AtomicInteger(0);
    private static final AtomicInteger totalCalls = new AtomicInteger(0);
    private static final AtomicInteger okCalls = new AtomicInteger(0);
    private static volatile String lastCallInfo = "—";
    private static volatile boolean lastProbeLoggedIn = false, lastProbeReady = false;

    // 会话上下文监控（估算 tokens）：跟踪当前官网会话累计上下文，达阈值自动轮换
    private static final Object ctxLock = new Object();
    private static volatile long sSessionTokens = 0L;
    private static volatile String sTrackedSessionId = "";
    private static volatile String sContextInfo = "—";
    private static final long DEFAULT_CONTEXT_TOKENS = 1000000L;
    /** 最近一次请求声明的工具名：部分客户端只在部分请求里带 tools，用它兜底校验。 */
    private static volatile java.util.Set<String> sLastToolNames = java.util.Collections.emptySet();
    private static final int DEFAULT_THRESHOLD_PCT = 70;

    public static void start(Context ctx) {
        Intent i = new Intent(ctx, ApiService.class).setAction(ACTION_START);
        if (Build.VERSION.SDK_INT >= 26) ctx.startForegroundService(i);
        else ctx.startService(i);
    }

    public static void stop(Context ctx) {
        ctx.stopService(new Intent(ctx, ApiService.class));
    }

    public static String state() { return sState; }
    public static String isLoggedInText() { return lastProbeLoggedIn ? "已登录" : "未登录（请在「对话页」登录）"; }
    public static String totalCallsText() { return totalCalls.get() + " 次（成功 " + okCalls.get() + "）"; }
    public static String lastCallText() { return lastCallInfo; }

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override
    public void onCreate() {
        super.onCreate();
        sInstance = this;
        serviceRunning = true;
        createChannel();
        startForegroundCompat();
        try {
            PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "DSWebAPI::svc");
            wakeLock.setReferenceCounted(false);
            wakeLock.acquire(24 * 60 * 60 * 1000L);
        } catch (Throwable ignored) {}
        KeepAlive.scheduleHeartbeat(this);

        // 读取 bridge.js 并确保 WebView 存在（服务侧持有，Activity 关闭后仍运行）
        String bridgeJs = WebHost.loadBridgeJs(this);
        WebHost.ensure(this, bridgeJs);

        startHttp();
        startWatchdog();
        KeepAliveJobService.schedule(this);
        maybeShowFloatingBall();
        updateNotification();
        Util.log("ApiService 已启动");
    }

    private void startHttp() {
        final int port = port();
        try {
            server = new HttpBridgeServer(port, this::route);
            server.start();
        } catch (Exception e) {
            Util.log("HTTP 启动失败(端口 " + port + "): " + e.getMessage());
            // 端口占用则顺延
            for (int p = port + 1; p < port + 20; p++) {
                try {
                    server = new HttpBridgeServer(p, this::route);
                    server.start();
                    Util.prefs(this).edit().putInt("port_active", p).apply();
                    Util.log("改用端口 " + p);
                    break;
                } catch (Exception ignored) {}
            }
        }
        lastPingOk = System.currentTimeMillis();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? ACTION_START : intent.getAction();
        if (ACTION_STOP.equals(action)) {
            Util.prefs(this).edit().putBoolean("service_enabled", false).apply();
            KeepAliveJobService.cancel(this);
            stopSelf();
            return START_NOT_STICKY;
        }
        Util.prefs(this).edit().putBoolean("service_enabled", true).apply();
        KeepAlive.scheduleHeartbeat(this);
        return START_STICKY;
    }

    private final Runnable watchdogTask = new Runnable() {
        @Override public void run() {
            try {
                if (server == null) startHttp();
                if (wakeLock != null && !wakeLock.isHeld()) wakeLock.acquire(24 * 60 * 60 * 1000L);
                if (WebHost.get() == null) {
                    WebHost.ensure(ApiService.this, WebHost.loadBridgeJs(ApiService.this));
                } else {
                    DeepSeekController.get().injectBridge();
                }
                updateNotification();
                sState = WebHost.isReady() ? (lastProbeLoggedIn ? "运行中（已就绪）" : "运行中（等待登录）") : "运行中（页面加载中）";
                lastPingOk = System.currentTimeMillis();
            } catch (Exception e) {
                Util.log("看门狗异常: " + e.getMessage());
            }
            watchdog.postDelayed(this, 60000L);
        }
    };

    private void startWatchdog() { watchdog.removeCallbacks(watchdogTask); watchdog.postDelayed(watchdogTask, 60000L); }

    @Override public void onTaskRemoved(Intent rootIntent) {
        KeepAlive.scheduleServiceRestart(this, 1000L);
        super.onTaskRemoved(rootIntent);
    }

    @Override
    public void onDestroy() {
        boolean intentional = !Util.prefs(this).getBoolean("service_enabled", false);
        if (sInstance == this) sInstance = null;
        serviceRunning = false;
        sState = "已停止";
        watchdog.removeCallbacks(watchdogTask);
        hideFloatingBall();
        if (server != null) { server.stop(); server = null; }
        if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        chatExec.shutdownNow();
        pingExec.shutdownNow();
        if (intentional) { KeepAlive.cancelHeartbeat(this); WebHost.destroy(); }
        else KeepAlive.scheduleServiceRestart(this, 3000L);
        Util.log("ApiService 已销毁");
        super.onDestroy();
    }

    /* ================= bridge 状态回调 ================= */

    public static void onBridgeStatus(String json) {
        try {
            JSONObject o = new JSONObject(json);
            String type = o.optString("type");
            if ("probe".equals(type) || "boot".equals(type)) {
                if (o.has("loggedIn")) lastProbeLoggedIn = o.optBoolean("loggedIn");
                if (o.has("ready")) lastProbeReady = o.optBoolean("ready");
            } else if ("session".equals(type)) {
                String sid = o.optString("sessionId", "");
                synchronized (ctxLock) {
                    if (!sid.equals(sTrackedSessionId)) {
                        sTrackedSessionId = sid;
                        sSessionTokens = 0;
                        sContextInfo = "新会话 · 0";
                        Util.log("会话切换 → 上下文计数重置" + (sid.isEmpty() ? "" : " (" + shortId(sid) + ")"));
                    }
                }
            }
        } catch (Exception ignored) {}
    }

    /* ================= 会话上下文监控 ================= */

    public static String contextInfoText() { return sContextInfo; }

    /** 手动新建对话后重置上下文计数。 */
    public static void resetContext() {
        synchronized (ctxLock) {
            sSessionTokens = 0;
            sTrackedSessionId = "";
            sContextInfo = "0 / " + fmtTokens(DEFAULT_CONTEXT_TOKENS);
        }
    }

    private long contextLimitTokens() {
        int v = Util.prefs(this).getInt("context_tokens", (int) DEFAULT_CONTEXT_TOKENS);
        if (v < 8000) v = 8000;
        return v;
    }

    private int newChatThresholdPct() {
        int v = Util.prefs(this).getInt("newchat_threshold", DEFAULT_THRESHOLD_PCT);
        if (v < 10) v = 10;
        if (v > 100) v = 100;
        return v;
    }

    private boolean autoNewChat() { return Util.prefs(this).getBoolean("auto_newchat", true); }

    /** 单请求 prompt 字符上限（0=不限；网页端硬上限约 2621440 字符，默认留约 43% 余量）。 */
    private int maxPromptChars() {
        return Math.max(0, Util.prefs(this).getInt("max_prompt_chars", 1500000));
    }

    /** 单请求图片附件数量上限（0=不限；网页端实测成功 40 张、失败 52 张，默认取 40）。 */
    private int maxRefImages() {
        return Math.max(0, Util.prefs(this).getInt("max_ref_images", 40));
    }

    /** 错误里带「上下文超限」标记（网页端自然语言错误）。 */
    static boolean lookContextLimit(String err) {
        if (err == null || err.isEmpty()) return false;
        if (err.toUpperCase(java.util.Locale.ROOT).contains("CONTEXT_LIMIT")) return true;
        if (err.contains("上下文") && (err.contains("上限") || err.contains("超出") || err.contains("过长") || err.contains("达到"))) return true;
        if (err.contains("长度上限") || err.contains("达到最大长度") || err.contains("输入过长")
                || err.contains("内容过长") || err.contains("文本过长") || err.contains("字数上限")) return true;
        String low = err.toLowerCase(java.util.Locale.ROOT);
        return (low.contains("too long") || low.contains("too large") || low.contains("exceed"))
                && (low.contains("content") || low.contains("prompt") || low.contains("context")
                    || low.contains("input") || low.contains("message") || low.contains("text") || low.contains("token"));
    }

    /** 错误里带「附件/引用文件过多」标记（网页端 biz_code 10 / too many ref file）。 */
    static boolean lookTooManyRefFiles(String err) {
        if (err == null || err.isEmpty()) return false;
        if (err.contains("附件") && (err.contains("过多") || err.contains("上限") || err.contains("超出") || err.contains("超限"))) return true;
        if (err.contains("引用文件") || err.contains("文件数量")) return true;
        String low = err.toLowerCase(java.util.Locale.ROOT);
        return low.contains("too many ref") || low.contains("ref file");
    }

    /** 官网把「上下文超限，请开启新对话」当成一条普通回复返回时的识别（严格限长，避免误判正常回答）。 */
    static boolean lookContextLimitText(String content, String thinking) {
        if (content == null || content.isEmpty()) return false;
        if (thinking != null && !thinking.isEmpty()) return false;   // 有思考内容 = 正常回答
        String c = content.trim();
        if (c.length() > 120) return false;
        if (lookContextLimit(c)) return true;
        return c.contains("请开启新对话") || c.contains("请新建对话") || c.contains("开启新对话后");
    }

    /** 粗略估算 tokens：中日韩字符 ≈1.35 token/字，其余 ≈4 字符/token。 */
    private static long estTokens(String s) {
        if (s == null || s.isEmpty()) return 0;
        long cjk = 0, other = 0;
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF)
                    || (c >= 0x3040 && c <= 0x30FF) || (c >= 0x3000 && c <= 0x303F)
                    || (c >= 0xFF00 && c <= 0xFFEF)) cjk++;
            else other++;
        }
        return Math.round(cjk * 1.35 + other / 4.0);
    }

    private static String fmtTokens(long t) {
        if (t >= 10000) return String.format(java.util.Locale.CHINA, "%.1f万", t / 10000.0);
        return String.valueOf(t);
    }

    private static String shortId(String sid) {
        if (sid == null || sid.isEmpty()) return "";
        return sid.length() <= 8 ? sid : sid.substring(0, 8);
    }

    /** 每轮回复结束后累计会话上下文；达到阈值则自动新建对话并尽力删除旧会话。 */
    private void afterReply(JSONObject r, String promptText) {
        try {
            long promptTok = estTokens(promptText);
            String sid = r == null ? "" : r.optString("sessionId", "");
            long replyTok = r == null ? 0 : (estTokens(r.optString("content", "")) + estTokens(r.optString("thinking", "")));
            long used, limit;
            int pct;
            boolean rotate = false;
            synchronized (ctxLock) {
                if (!sid.isEmpty() && !sid.equals(sTrackedSessionId)) { sTrackedSessionId = sid; sSessionTokens = 0; }
                sSessionTokens += promptTok + replyTok;
                used = sSessionTokens;
                limit = contextLimitTokens();
                pct = newChatThresholdPct();
                long threshold = limit * pct / 100;
                if (autoNewChat() && threshold > 0 && used >= threshold && inflight.get() <= 1) {
                    rotate = true;
                    sSessionTokens = 0;
                    sTrackedSessionId = "";
                }
                sContextInfo = fmtTokens(used) + " / " + fmtTokens(limit) + "（" + (limit > 0 ? used * 100 / limit : 0) + "%）";
            }
            Util.log("会话上下文 ≈ " + used + " tokens / " + limit + "（阈值 " + pct + "%）" + (rotate ? " → 自动新建对话" : ""));
            if (rotate) rotateSession();
            updateNotification();
        } catch (Exception ignored) {}
    }

    /** 自动轮换：上下文达阈值时点击「新建对话」（旧会话不自动删除，由用户自行处理）。 */
    private void rotateSession() {
        chatExec.submit(() -> {
            try {
                JSONObject n = DeepSeekController.get().newChat();
                Util.log("自动新建对话: " + (n.optBoolean("ok") ? "成功" : "失败(" + n.optString("error") + ")"));
            } catch (Exception e) {
                Util.log("自动新建对话异常: " + e.getMessage());
            }
            updateNotification();
        });
    }

    /* ================= HTTP 路由 ================= */

    private void route(HttpBridgeServer.Request req, HttpBridgeServer.Response res) throws Exception {
        String p = req.path;
        lastPingOk = System.currentTimeMillis();
        if ("/".equals(p) || "/health".equals(p) || "/v1".equals(p) || "/v1/".equals(p)) { health(res); return; }
        if ("/v1/models".equals(p) || "/models".equals(p)) {
            if (!auth(req, res)) return;
            res.sendJson(OpenAiAdapter.modelsList().toString());
            return;
        }
        if ("/v1/chat/completions".equals(p) || "/chat/completions".equals(p) || "/v1/completions".equals(p)) {
            if (!auth(req, res)) return;
            if (!"POST".equals(req.method)) { res.sendJson(405, errJson("Method not allowed", "invalid_request_error")); return; }
            handleChat(req, res);
            return;
        }
        res.sendJson(404, errJson("Not found: " + p, "invalid_request_error"));
    }

    private void health(HttpBridgeServer.Response res) throws Exception {
        JSONObject o = new JSONObject();
        o.put("status", "ok");
        o.put("service", "deepseek-web-api");
        o.put("running", serviceRunning);
        o.put("page_ready", WebHost.isReady());
        o.put("bridge_attached", DeepSeekController.get().isAttached());
        o.put("logged_in", lastProbeLoggedIn);
        o.put("inflight", inflight.get());
        o.put("total_calls", totalCalls.get());
        o.put("ok_calls", okCalls.get());
        o.put("endpoint", "http://127.0.0.1:" + port() + "/v1");
        res.sendJson(o.toString());
    }

    private boolean auth(HttpBridgeServer.Request req, HttpBridgeServer.Response res) throws Exception {
        String key = apiKey();
        if (key == null || key.isEmpty()) return true;
        String got = req.bearer();
        if (key.equals(got)) return true;
        res.sendJson(401, errJson("Incorrect API key provided.", "invalid_request_error"));
        return false;
    }

    /* ================= 聊天补全 ================= */

    private void handleChat(final HttpBridgeServer.Request req, final HttpBridgeServer.Response res) {
        JSONObject body;
        try { body = new JSONObject(req.body == null ? "{}" : req.body); }
        catch (Exception e) { try { res.sendJson(400, errJson("Invalid JSON body", "invalid_request_error")); } catch (Exception ignored) {} return; }

        if (!DeepSeekController.get().isAttached()) {
            try { res.sendJson(503, errJson("网页桥未就绪：请保持 App 运行并在「对话页」登录 DeepSeek 官网。", "server_error")); } catch (Exception ignored) {}
            return;
        }

        final String model = body.optString("model", "deepseek");
        final boolean stream = body.optBoolean("stream", false);
        final boolean stateless = Util.prefs(this).getBoolean("stateless", true);

        final PromptBuilder.Result pb;
        try { pb = PromptBuilder.build(body, stateless, maxPromptChars(), maxRefImages()); }
        catch (Exception e) { try { res.sendJson(400, errJson("Bad request: " + e.getMessage(), "invalid_request_error")); } catch (Exception ignored) {} return; }

        // 上限保护提示（超长 prompt 中段截断 / 图片附件超出数量上限只保留最近 N 张）
        if (pb.truncated) Util.log("prompt 超长，已中段截断：省略约 " + pb.omittedChars + " 字符（保留系统指令与最近对话）");
        if (pb.omittedImages > 0) Util.log("附件图片超出上限，已略过最早 " + pb.omittedImages + " 张（本次只带 " + pb.attachments.size() + " 张）");

        if (!pb.toolNames.isEmpty()) sLastToolNames = new java.util.LinkedHashSet<>(pb.toolNames);
        final java.util.Set<String> toolNames = pb.toolNames.isEmpty() ? sLastToolNames : new java.util.LinkedHashSet<>(pb.toolNames);
        // 工具声明的 JSON Schema：用于按类型归一化参数（声明为 string 的参数不得传对象/数组）
        final JSONArray toolDefs = body.optJSONArray("tools");

        if (pb.text.trim().isEmpty() && pb.attachments.isEmpty()) {
            try { res.sendJson(400, errJson("messages 为空", "invalid_request_error")); } catch (Exception ignored) {}
            return;
        }

        // 思考/搜索策略
        String thinking = Util.prefs(this).getString("thinking_mode", "on");
        String search = Util.prefs(this).getString("search_mode", "off");
        String re = body.optString("reasoning_effort", "");
        if ("none".equalsIgnoreCase(re)) thinking = "off";
        else if (!re.isEmpty()) thinking = "on";
        else if (model.toLowerCase().contains("reasoner") || model.toLowerCase().contains("think")) thinking = "on";
        DeepSeekController.get().configure(thinking, search);

        inflight.incrementAndGet();
        totalCalls.incrementAndGet();
        final long t0 = System.currentTimeMillis();
        updateNotification();

        chatExec.submit(() -> {
            try {
                // 附件：逐个注入官网，等待上传/解析
                for (PromptBuilder.Attachment a : pb.attachments) {
                    JSONObject r = DeepSeekController.get().attachFile(a.name, a.mime, a.base64, 150);
                    if (!r.optBoolean("ok")) Util.log("附件挂载失败: " + a.name + " " + r.optString("error"));
                }
                if (stream) runStream(model, pb, res, toolNames, toolDefs);
                else runBlocking(model, pb, res, toolNames, toolDefs);
                long cost = System.currentTimeMillis() - t0;
                okCalls.incrementAndGet();
                lastCallInfo = "成功 · " + (cost / 1000.0) + "s · " + (pb.attachments.size() > 0 ? pb.attachments.size() + "附件 · " : "");
            } catch (Exception e) {
                Util.log("chat 处理异常: " + e.getMessage());
                lastCallInfo = "异常: " + e.getMessage();
                try { res.sendJson(500, errJson(e.getMessage(), "server_error")); } catch (Exception ignored) {}
            } finally {
                if (!stream) { try { res.close(); } catch (Exception ignored) {} }
                inflight.decrementAndGet();
                updateNotification();
            }
        });
    }

    private void runBlocking(String model, PromptBuilder.Result pb, HttpBridgeServer.Response res,
                             java.util.Set<String> toolNames, JSONArray toolDefs) throws Exception {
        int timeout = Util.prefs(this).getInt("timeout", 300);
        JSONObject r = DeepSeekController.get().sendPrompt(pb.text, null, timeout);
        String thinking = r.optString("thinking");
        String content = r.optString("content");

        // 上下文超限 / 附件数量超限 → 自动新开对话并重试一次（此时还没写任何响应，重试是安全的）
        String err0 = r.optString("error", "");
        boolean hitLimit = lookContextLimit(err0) || lookContextLimitText(content, thinking);
        boolean hitRef = lookTooManyRefFiles(err0);
        if (hitLimit || hitRef) {
            Util.log((hitRef ? "命中附件数量上限" : "命中上下文上限") + " → 自动新开对话并重试（" + (err0.isEmpty() ? content : err0) + "）");
            JSONObject nc = DeepSeekController.get().newChat();
            if (nc.optBoolean("ok")) { try { Thread.sleep(500); } catch (InterruptedException ignored) {} }
            r = DeepSeekController.get().sendPrompt(pb.text, null, timeout);
            thinking = r.optString("thinking");
            content = r.optString("content");
        }

        if (content.isEmpty() && thinking.isEmpty()) {
            String err = r.optString("error", "EMPTY");
            res.sendJson(502, errJson("DeepSeek 未返回内容: " + err, "server_error"));
            lastCallInfo = "失败: " + err;
            return;
        }
        OpenAiAdapter.ChatResult cr = OpenAiAdapter.process(thinking, content, toolNames, toolDefs);
        String id = OpenAiAdapter.newId();
        long created = System.currentTimeMillis() / 1000;
        JSONObject out = OpenAiAdapter.buildCompletion(id, model, created, cr.thinking, cr.content, cr.toolCalls,
                cr.finishReason, OpenAiAdapter.estTokens(pb.text), OpenAiAdapter.estTokens(cr.content + cr.thinking));
        res.sendJson(out.toString());
        lastCallInfo = "成功 · " + (cr.toolCalls != null ? "工具调用" : cr.content.length() + "字")
                + (r.optBoolean("recalled") ? " · 防撤回" : "");
        afterReply(r, pb.text);
    }

    /* ================= 流式 ================= */

    private static final int HOLD = 32;

    private void runStream(String model, PromptBuilder.Result pb, HttpBridgeServer.Response res, final java.util.Set<String> toolNames, final JSONArray toolDefs) {
        int timeout = Util.prefs(this).getInt("timeout", 300);
        final String id = OpenAiAdapter.newId();
        final long created = System.currentTimeMillis() / 1000;

        final Object lock = new Object();
        final int[] lastTh = {0};
        final int[] emitted = {0};
        final boolean[] toolMode = {false};
        final boolean[] finished = {false};

        try { res.startSse(); } catch (Exception e) { return; }
        // 角色首帧
        writeChunk(res, lock, OpenAiAdapter.chunk(id, model, created, roleDelta(), null));
        // 心跳保活
        final ScheduledFuture<?>[] ping = new ScheduledFuture<?>[1];
        ping[0] = pingExec.scheduleAtFixedRate(() -> {
            synchronized (lock) { try { res.sseComment("ping"); } catch (Exception ignored) {} }
        }, 15, 15, TimeUnit.SECONDS);

        DeepSeekController.get().sendPromptStream(pb.text, null, timeout, new DeepSeekController.StreamListener() {
            @Override public void onDelta(String th, String ct, boolean recalled) {
                synchronized (lock) {
                    if (finished[0]) return;
                    try {
                        if (th.length() > lastTh[0]) {
                            String piece = th.substring(lastTh[0]);
                            lastTh[0] = th.length();
                            writeChunk(res, lock, OpenAiAdapter.chunk(id, model, created, delta("reasoning_content", piece), null));
                        }
                        if (toolMode[0]) { /* 工具块，静默缓冲，结束时统一下发 */ }
                        else {
                            // 只要出现工具标记信号（宽窄变体、孤立残片都算），就停在标记之前，
                            // 绝不让标记或残片上屏（旧实现只认精确 <|tool_calls_begin|>）。
                            int safe = ToolMarkup.safeEmitEnd(ct, HOLD);
                            if (safe > emitted[0]) {
                                writeChunk(res, lock, OpenAiAdapter.chunk(id, model, created, delta("content", ct.substring(emitted[0], safe)), null));
                                emitted[0] = safe;
                            }
                            if (ToolMarkup.earliestSignal(ct) >= 0) toolMode[0] = true;
                        }
                    } catch (Exception ignored) {}
                }
            }

            @Override public void onFinish(JSONObject r) {
                synchronized (lock) {
                    if (finished[0]) return;
                    finished[0] = true;
                    if (ping[0] != null) ping[0].cancel(false);
                    try {
                        String content = r.optString("content");
                        boolean ok = r.optBoolean("ok") || !content.isEmpty() || !r.optString("thinking").isEmpty();
                        if (!ok) {
                            // 错误：发一条错误内容后正常收尾
                            writeChunk(res, lock, OpenAiAdapter.chunk(id, model, created, delta("content", "\n[错误] " + r.optString("error", "UNKNOWN")), null));
                            writeChunk(res, lock, OpenAiAdapter.chunk(id, model, created, emptyDelta(), "stop"));
                            finishSse(res, lock);
                            lastCallInfo = "失败: " + r.optString("error");
                            return;
                        }
                        OpenAiAdapter.ChatResult cr = OpenAiAdapter.process(r.optString("thinking"), content, toolNames, toolDefs);
                        if (cr.toolCalls != null && cr.toolCalls.length() > 0) {
                            // 工具调用：content 中标签之前若还有未下发正文，先补发
                            String before = cr.content;
                            JSONArray tcs = OpenAiAdapter.toOpenAiToolCalls(cr.toolCalls);
                            for (int i = 0; i < tcs.length(); i++) {
                                JSONObject tc = tcs.optJSONObject(i);
                                JSONObject d = new JSONObject();
                                JSONArray arr = new JSONArray();
                                JSONObject item = new JSONObject();
                                try {
                                    item.put("index", i);
                                    item.put("id", tc.optString("id"));
                                    item.put("type", "function");
                                    item.put("function", tc.optJSONObject("function"));
                                } catch (Exception ignored) {}
                                arr.put(item);
                                d.put("tool_calls", arr);
                                writeChunk(res, lock, OpenAiAdapter.chunk(id, model, created, d, null));
                            }
                            writeChunk(res, lock, OpenAiAdapter.chunk(id, model, created, emptyDelta(), "tool_calls"));
                            lastCallInfo = "成功 · 工具调用 x" + tcs.length();
                        } else {
                            // 非有效工具调用：补发剩余正文。**必须先剥掉标记残片**，
                            // 否则「静默期之后」的残片会在这里逃逸成正文。
                            String tailText = content.length() > emitted[0] ? content.substring(emitted[0]) : "";
                            tailText = ToolMarkup.stripStray(tailText);
                            if (!tailText.isEmpty()) {
                                writeChunk(res, lock, OpenAiAdapter.chunk(id, model, created, delta("content", tailText), null));
                            }
                            if (toolMode[0]) Util.log("检测到标记但非有效工具调用，已按正文输出（残片已剥离）");
                            writeChunk(res, lock, OpenAiAdapter.chunk(id, model, created, emptyDelta(), "stop"));
                            lastCallInfo = "成功 · " + content.length() + "字" + (r.optBoolean("recalled") ? " · 防撤回" : "");
                        }
                        afterReply(r, pb.text);
                        finishSse(res, lock);
                    } catch (Exception e) {
                        Util.log("流式收尾异常: " + e.getMessage());
                        try { finishSse(res, lock); } catch (Exception ignored) {}
                    }
                }
            }
        });
    }

    private void finishSse(HttpBridgeServer.Response res, Object lock) {
        synchronized (lock) {
            try { res.sseData("[DONE]"); } catch (Exception ignored) {}
            try { res.endChunked(); } catch (Exception ignored) {}
            try { res.close(); } catch (Exception ignored) {}
        }
    }

    private void writeChunk(HttpBridgeServer.Response res, Object lock, String json) {
        synchronized (lock) {
            try { res.sseData(json); } catch (Exception ignored) {}
        }
    }

    private static JSONObject roleDelta() {
        JSONObject d = new JSONObject();
        try { d.put("role", "assistant"); d.put("content", ""); } catch (Exception ignored) {}
        return d;
    }
    private static JSONObject emptyDelta() { return new JSONObject(); }
    private static JSONObject delta(String key, String value) {
        JSONObject d = new JSONObject();
        try { d.put(key, value); } catch (Exception ignored) {}
        return d;
    }

    private static String errJson(String msg, String type) {
        JSONObject e = new JSONObject();
        try {
            JSONObject err = new JSONObject();
            err.put("message", msg);
            err.put("type", type);
            err.put("code", type);
            e.put("error", err);
        } catch (Exception ignored) {}
        return e.toString();
    }

    /* ================= 配置 ================= */

    private int port() {
        SharedPreferences sp = Util.prefs(this);
        int p = sp.getInt("port_active", 0);
        if (p > 0) return p;
        return sp.getInt("port", 8787);
    }
    private String apiKey() { return Util.prefs(this).getString("api_key", "sk-deepseek"); }

    /* ================= 通知 ================= */

    private void createChannel() {
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            if (nm.getNotificationChannel(CHANNEL_ID) == null) {
                NotificationChannel ch = new NotificationChannel(CHANNEL_ID, "DeepSeek API 桥", NotificationManager.IMPORTANCE_LOW);
                ch.setShowBadge(false);
                nm.createNotificationChannel(ch);
            }
        }
    }

    private void startForegroundCompat() {
        Notification n = buildNotification();
        if (Build.VERSION.SDK_INT >= 34) {
            int types = android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC
                    | android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE;
            try {
                startForeground(NOTIF_ID, n, types);
            } catch (SecurityException e) {
                // manifest 缺声明/权限时回退，绝不因前台类型导致启动失败
                Util.log("specialUse 前台失败，回退 dataSync: " + e.getMessage());
                startForeground(NOTIF_ID, n, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
            }
        } else if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIF_ID, n, 1);
        } else {
            startForeground(NOTIF_ID, n);
        }
    }

    private void updateNotification() {
        try {
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            nm.notify(NOTIF_ID, buildNotification());
        } catch (Exception ignored) {}
        // 悬浮球动效跟随「是否有请求进行中」
        final FloatingBallView bv = ballView;
        if (bv != null) {
            final boolean busy = inflight.get() > 0;
            bv.post(new Runnable() { @Override public void run() { bv.setBusy(busy); } });
        }
    }

    /* ================= 悬浮球 ================= */

    private volatile FloatingBallView ballView;

    /** 是否允许悬浮窗（Android 6+ 需用户授权 SYSTEM_ALERT_WINDOW）。 */
    public static boolean canOverlay(Context ctx) {
        if (Build.VERSION.SDK_INT < 23) return true;
        return android.provider.Settings.canDrawOverlays(ctx);
    }

    private void maybeShowFloatingBall() {
        if (ballView != null) return;
        if (!Util.prefs(this).getBoolean("floating_ball", false)) return;
        if (!canOverlay(this)) { Util.log("悬浮球：缺少悬浮窗权限，已跳过"); return; }
        try {
            ballView = new FloatingBallView(this, new FloatingBallView.Listener() {
                @Override public void onTap() {
                    try {
                        Intent i = new Intent(ApiService.this, MainActivity.class);
                        i.setFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP);
                        startActivity(i);
                    } catch (Exception ignored) {}
                }
            });
            WindowManager wm = (WindowManager) getSystemService(WINDOW_SERVICE);
            wm.addView(ballView, ballView.params());
            ballView.setBusy(inflight.get() > 0);
        } catch (Exception e) {
            Util.log("悬浮球创建失败: " + e.getMessage());
            ballView = null;
        }
    }

    /** 供界面开关调用（主线程）。 */
    public void showFloatingBall() { maybeShowFloatingBall(); }

    public void hideFloatingBall() {
        final FloatingBallView bv = ballView;
        ballView = null;
        if (bv == null) return;
        try { ((WindowManager) getSystemService(WINDOW_SERVICE)).removeView(bv); } catch (Exception ignored) {}
    }

    private Notification buildNotification() {
        Intent open = new Intent(this, MainActivity.class);
        open.setFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP);
        int piFlags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= 23 ? PendingIntent.FLAG_IMMUTABLE : 0);
        PendingIntent contentPi = PendingIntent.getActivity(this, 1, open, piFlags);

        Intent stop = new Intent(this, ApiService.class).setAction(ACTION_STOP);
        PendingIntent stopPi = PendingIntent.getService(this, 2, stop, piFlags);

        String stateText;
        if (!WebHost.isReady()) stateText = "网页加载中…";
        else if (lastProbeLoggedIn) stateText = "已就绪";
        else stateText = "等待登录 DeepSeek";

        String text = "端口 " + port() + " · " + stateText + " · 调用 " + totalCalls.get()
                + (inflight.get() > 0 ? "（进行中 " + inflight.get() + "）" : "")
                + "\n上下文: " + sContextInfo
                + "\n最近: " + lastCallInfo;

        Notification.Builder b;
        if (Build.VERSION.SDK_INT >= 26) b = new Notification.Builder(this, CHANNEL_ID);
        else b = new Notification.Builder(this);
        b.setContentTitle("DeepSeek Web API 运行中")
                .setContentText(text)
                .setStyle(new Notification.BigTextStyle().bigText(text))
                .setSmallIcon(android.R.drawable.stat_sys_upload_done)
                .setOngoing(true)
                .setContentIntent(contentPi)
                .addAction(0, "停止", stopPi);
        return b.build();
    }
}
