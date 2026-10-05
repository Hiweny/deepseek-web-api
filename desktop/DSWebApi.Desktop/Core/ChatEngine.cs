using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace DSWebApi.Desktop.Core;

/// <summary>
/// 引擎：承载本地 OpenAI 兼容 HTTP 接口，把请求桥接到官网 WebView。
/// 对应 APK 的 ApiService（去掉 Android 通知/前台服务，改为桌面托盘 + 看门狗保活）。
/// </summary>
public sealed class ChatEngine : HttpServer.IRouter, WebBridge.IStatusListener
{
    public static readonly ChatEngine I = new ChatEngine();

    /* ---------------- 对外状态 ---------------- */

    private volatile bool _serviceRunning;
    private volatile string _state = "未启动";

    private volatile bool _lastProbeLoggedIn;
    private volatile bool _lastProbeReady;

    private int _inflight;
    private int _totalCalls;
    private int _okCalls;
    private volatile string _lastCallInfo = "—";

    public bool ServiceRunning => _serviceRunning;
    public string State => _state;
    public bool LoggedIn => _lastProbeLoggedIn;
    public bool PageReady => _lastProbeReady;
    public int TotalCalls => Volatile.Read(ref _totalCalls);
    public int OkCalls => Volatile.Read(ref _okCalls);
    public int Inflight => Volatile.Read(ref _inflight);
    public string LastCallInfo => _lastCallInfo;
    public int Port { get; private set; }
    public string LanUrl { get; private set; } = "";

    public string IsLoggedInText => _lastProbeLoggedIn ? "已登录" : "未登录（请在「对话页」登录 DeepSeek 官网）";
    public string TotalCallsText => TotalCalls + " 次（成功 " + OkCalls + "）";

    /* ---------------- 会话上下文监控 ---------------- */

    private const long DEFAULT_CONTEXT_TOKENS = 1000000L;
    private const int DEFAULT_THRESHOLD_PCT = 70;
    private readonly object _ctxLock = new object();
    private long _sessionTokens;
    private string _trackedSessionId = "";
    private volatile string _contextInfo = "—";
    private volatile HashSet<string> _lastToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string ContextInfoText => _contextInfo;

    /* ---------------- 内部 ---------------- */

    private HttpServer _server;
    private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
    private System.Threading.Timer _watchdog;

    /// <summary>统计/状态变化时回调（UI 刷新）。</summary>
    public event Action Changed;

    private ChatEngine()
    {
        var t = new Thread(WorkerLoop) { IsBackground = true, Name = "chat-worker" };
        t.Start();
    }

    private void Notify()
    {
        try { Changed?.Invoke(); } catch { }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var act in _queue.GetConsumingEnumerable())
            {
                try { act(); } catch (Exception e) { Log.Write("chat 任务异常: " + e.Message); }
            }
        }
        catch (Exception) { }
    }

    /* ================= 生命周期 ================= */

    public void StartAll(int desiredPort)
    {
        if (_server != null && _server.Running)
        {
            _serviceRunning = true;
            UpdateState();
            Notify();
            return;   // 幂等：已在运行则不重复监听（避免端口被自己占用而顺延）
        }
        StartHttp(desiredPort);
        StartWatchdog();
        _serviceRunning = true;
        _state = "运行中";
        UpdateState();
        Log.Write("引擎已启动");
        Notify();
    }

    public void StopAll()
    {
        _serviceRunning = false;
        _state = "已停止";
        try { _watchdog?.Dispose(); } catch { }
        _watchdog = null;
        try { _server?.Stop(); } catch { }
        _server = null;
        Log.Write("引擎已停止");
        Notify();
    }

    public void StartHttp(int desiredPort)
    {
        try
        {
            _server = new HttpServer(desiredPort, this);
            _server.Start();
            Port = desiredPort;
            LanUrl = "http://" + FirstLanIp() + ":" + desiredPort + "/v1";
            return;
        }
        catch (Exception e)
        {
            Log.Write("HTTP 启动失败(端口 " + desiredPort + "): " + e.Message);
        }
        // 端口占用则顺延
        for (int p = desiredPort + 1; p < desiredPort + 20; p++)
        {
            try
            {
                _server = new HttpServer(p, this);
                _server.Start();
                Port = p;
                LanUrl = "http://" + FirstLanIp() + ":" + p + "/v1";
                Log.Write("改用端口 " + p);
                return;
            }
            catch (Exception) { }
        }
        Log.Write("!! 所有候选端口都不可用，HTTP 服务未能启动");
    }

    private void StartWatchdog()
    {
        _watchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                if (_server == null || !_server.Running) StartHttp(Prefs.Port);
                UpdateState();
                Notify();
            }
            catch (Exception e) { Log.Write("看门狗异常: " + e.Message); }
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public void UpdateState()
    {
        if (!_serviceRunning) { _state = "已停止"; return; }
        if (!WebBridge.I.IsReady) _state = "运行中（页面加载中）";
        else _state = _lastProbeLoggedIn ? "运行中（已就绪）" : "运行中（等待登录）";
    }

    public static string FirstLanIp()
    {
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)) return ip.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public static List<string> AllLanIps()
    {
        var list = new List<string>();
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)) list.Add(ip.ToString());
            }
        }
        catch { }
        if (list.Count == 0) list.Add("127.0.0.1");
        return list;
    }

    /* ================= bridge 状态回调 ================= */

    public void OnStatus(string json)
    {
        try
        {
            var o = Json.TryParse(json) as JObj;
            if (o == null) return;
            string type = o.Str("type");
            if (type == "probe" || type == "boot")
            {
                if (o.Has("loggedIn")) _lastProbeLoggedIn = o.Bool("loggedIn");
                if (o.Has("ready")) _lastProbeReady = o.Bool("ready");
                UpdateState();
                Notify();
            }
            else if (type == "session")
            {
                string sid = o.Str("sessionId", "");
                lock (_ctxLock)
                {
                    if (sid != _trackedSessionId)
                    {
                        _trackedSessionId = sid;
                        _sessionTokens = 0;
                        _contextInfo = "新会话 · 0";
                        Log.Write("会话切换 → 上下文计数重置" + (sid.Length == 0 ? "" : " (" + ShortId(sid) + ")"));
                    }
                }
                Notify();
            }
        }
        catch (Exception) { }
    }

    /* ================= 会话上下文监控 ================= */

    /// <summary>手动新建对话后重置上下文计数。</summary>
    public void ResetContext()
    {
        lock (_ctxLock)
        {
            _sessionTokens = 0;
            _trackedSessionId = "";
            _contextInfo = "0 / " + FmtTokens(ContextLimitTokens());
        }
        Notify();
    }

    private long ContextLimitTokens()
    {
        int v = Prefs.ContextTokens;
        if (v < 8000) v = 8000;
        return v;
    }

    private int NewChatThresholdPct()
    {
        int v = Prefs.NewChatThreshold;
        if (v < 10) v = 10;
        if (v > 100) v = 100;
        return v;
    }

    /// <summary>粗略估算 tokens：中日韩字符 ≈1.35 token/字，其余 ≈4 字符/token。</summary>
    private static long EstTokens(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        long cjk = 0, other = 0;
        foreach (char c in s)
        {
            if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF)
                || (c >= 0x3040 && c <= 0x30FF) || (c >= 0x3000 && c <= 0x303F)
                || (c >= 0xFF00 && c <= 0xFFEF)) cjk++;
            else other++;
        }
        return (long)Math.Round(cjk * 1.35 + other / 4.0);
    }

    private static string FmtTokens(long t)
    {
        if (t >= 10000) return (t / 10000.0).ToString("F1") + "万";
        return t.ToString();
    }

    private static string ShortId(string sid)
    {
        if (string.IsNullOrEmpty(sid)) return "";
        return sid.Length <= 8 ? sid : sid.Substring(0, 8);
    }

    /// <summary>每轮回复结束后累计会话上下文；达到阈值则自动新建对话。</summary>
    private void AfterReply(JObj r, string promptText)
    {
        try
        {
            long promptTok = EstTokens(promptText);
            string sid = r == null ? "" : r.Str("sessionId", "");
            long replyTok = r == null ? 0 : (EstTokens(r.Str("content")) + EstTokens(r.Str("thinking")));
            long used, limit;
            int pct;
            bool rotate = false;
            lock (_ctxLock)
            {
                if (sid.Length > 0 && sid != _trackedSessionId) { _trackedSessionId = sid; _sessionTokens = 0; }
                _sessionTokens += promptTok + replyTok;
                used = _sessionTokens;
                limit = ContextLimitTokens();
                pct = NewChatThresholdPct();
                long threshold = limit * pct / 100;
                if (Prefs.AutoNewChat && threshold > 0 && used >= threshold && Volatile.Read(ref _inflight) <= 1)
                {
                    rotate = true;
                    _sessionTokens = 0;
                    _trackedSessionId = "";
                }
                _contextInfo = FmtTokens(used) + " / " + FmtTokens(limit) + "（" + (limit > 0 ? used * 100 / limit : 0) + "%）";
            }
            Log.Write("会话上下文 ≈ " + used + " tokens / " + limit + "（阈值 " + pct + "%）" + (rotate ? " → 自动新建对话" : ""));
            if (rotate) RotateSession();
            Notify();
        }
        catch (Exception) { }
    }

    /// <summary>自动轮换：上下文达阈值时点击「新建对话」（旧会话不自动删除，由用户自行处理）。</summary>
    private void RotateSession()
    {
        _queue.Add(() =>
        {
            try
            {
                var n = WebBridge.I.NewChat();
                Log.Write("自动新建对话: " + (n.Bool("ok") ? "成功" : "失败(" + n.Str("error") + ")"));
            }
            catch (Exception e) { Log.Write("自动新建对话异常: " + e.Message); }
            Notify();
        });
    }

    /* ================= HTTP 路由 ================= */

    public void Handle(HttpServer.Request req, HttpServer.Response res)
    {
        string p = req.Path;
        if (p == "/" || p == "/health" || p == "/v1" || p == "/v1/") { Health(res); return; }
        if (p == "/v1/models" || p == "/models")
        {
            if (!Auth(req, res)) return;
            res.SendJson(OpenAiAdapter.ModelsList().ToJson());
            return;
        }
        if (p == "/v1/chat/completions" || p == "/chat/completions" || p == "/v1/completions")
        {
            if (!Auth(req, res)) return;
            if (req.Method != "POST") { res.SendJson(405, ErrJson("Method not allowed", "invalid_request_error")); return; }
            HandleChat(req, res);
            return;
        }
        res.SendJson(404, ErrJson("Not found: " + p, "invalid_request_error"));
    }

    private void Health(HttpServer.Response res)
    {
        var o = new JObj();
        o.Set("status", "ok");
        o.Set("service", "deepseek-web-api");
        o.Set("platform", "windows");
        o.Set("running", _serviceRunning);
        o.Set("page_ready", WebBridge.I.IsReady);
        o.Set("bridge_attached", WebBridge.I.IsAttached);
        o.Set("logged_in", _lastProbeLoggedIn);
        o.Set("inflight", (long)Inflight);
        o.Set("total_calls", (long)TotalCalls);
        o.Set("ok_calls", (long)OkCalls);
        o.Set("port", (long)Port);
        o.Set("endpoint", "http://127.0.0.1:" + Port + "/v1");
        o.Set("lan_endpoint", LanUrl);
        res.SendJson(o.ToJson());
    }

    private bool Auth(HttpServer.Request req, HttpServer.Response res)
    {
        string key = Prefs.ApiKey;
        if (string.IsNullOrEmpty(key)) return true;
        string got = req.Bearer();
        if (key == got) return true;
        res.SendJson(401, ErrJson("Incorrect API key provided.", "invalid_request_error"));
        return false;
    }

    /* ================= 聊天补全 ================= */

    private void HandleChat(HttpServer.Request req, HttpServer.Response res)
    {
        JObj body;
        try { body = Json.ParseStrict(string.IsNullOrEmpty(req.Body) ? "{}" : req.Body) as JObj; }
        catch (Exception) { res.SendJson(400, ErrJson("Invalid JSON body", "invalid_request_error")); return; }
        if (body == null) { res.SendJson(400, ErrJson("Invalid JSON body", "invalid_request_error")); return; }

        if (!WebBridge.I.IsAttached)
        {
            res.SendJson(503, ErrJson("网页桥未就绪：请保持本程序运行并在「对话页」登录 DeepSeek 官网。", "server_error"));
            return;
        }

        string model = body.Str("model", "deepseek");
        bool stream = body.Bool("stream", false);
        bool stateless = Prefs.Stateless;

        PromptBuilder.Result pb;
        try { pb = PromptBuilder.Build(body, stateless); }
        catch (Exception e) { res.SendJson(400, ErrJson("Bad request: " + e.Message, "invalid_request_error")); return; }

        if (pb.toolNames.Count > 0) _lastToolNames = new HashSet<string>(pb.toolNames, StringComparer.OrdinalIgnoreCase);
        var toolNames = pb.toolNames.Count > 0 ? new HashSet<string>(pb.toolNames, StringComparer.OrdinalIgnoreCase) : _lastToolNames;

        if (pb.text.Trim().Length == 0 && pb.attachments.Count == 0)
        {
            res.SendJson(400, ErrJson("messages 为空", "invalid_request_error"));
            return;
        }

        // 思考/搜索策略
        string thinking = Prefs.ThinkingMode;
        string search = Prefs.SearchMode;
        string re = body.Str("reasoning_effort", "");
        if (re.Equals("none", StringComparison.OrdinalIgnoreCase)) thinking = "off";
        else if (re.Length > 0) thinking = "on";
        else if (model.ToLowerInvariant().Contains("reasoner") || model.ToLowerInvariant().Contains("think")) thinking = "on";
        WebBridge.I.Configure(thinking, search);

        Interlocked.Increment(ref _inflight);
        Interlocked.Increment(ref _totalCalls);
        long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Notify();

        _queue.Add(() =>
        {
            try
            {
                // 附件：逐个注入官网，等待上传/解析
                foreach (var a in pb.attachments)
                {
                    var r = WebBridge.I.AttachFile(a.Name, a.Mime, a.Base64, 150);
                    if (!r.Bool("ok")) Log.Write("附件挂载失败: " + a.Name + " " + r.Str("error"));
                }
                if (stream) RunStream(model, pb, res, toolNames);
                else RunBlocking(model, pb, res, toolNames);
                long cost = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t0;
                Interlocked.Increment(ref _okCalls);
                _lastCallInfo = "成功 · " + (cost / 1000.0).ToString("F1") + "s · " + (pb.attachments.Count > 0 ? pb.attachments.Count + "附件 · " : "");
            }
            catch (Exception e)
            {
                Log.Write("chat 处理异常: " + e.Message);
                _lastCallInfo = "异常: " + e.Message;
                try { res.SendJson(500, ErrJson(e.Message, "server_error")); } catch { }
            }
            finally
            {
                if (!stream) { try { res.Close(); } catch { } }
                Interlocked.Decrement(ref _inflight);
                Notify();
            }
        });
    }

    private void RunBlocking(string model, PromptBuilder.Result pb, HttpServer.Response res, HashSet<string> toolNames)
    {
        int timeout = Prefs.TimeoutSec;
        var r = WebBridge.I.SendPrompt(pb.text, null, timeout);
        string thinking = r.Str("thinking");
        string content = r.Str("content");
        if (content.Length == 0 && thinking.Length == 0)
        {
            string err = r.Str("error", "EMPTY");
            res.SendJson(502, ErrJson("DeepSeek 未返回内容: " + err, "server_error"));
            _lastCallInfo = "失败: " + err;
            return;
        }
        var cr = OpenAiAdapter.Process(thinking, content, toolNames);
        string id = OpenAiAdapter.NewId();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var outObj = OpenAiAdapter.BuildCompletion(id, model, created, cr.thinking, cr.content, cr.toolCalls,
            cr.finishReason, OpenAiAdapter.EstTokens(pb.text), OpenAiAdapter.EstTokens(cr.content + cr.thinking));
        res.SendJson(outObj.ToJson());
        _lastCallInfo = "成功 · " + (cr.toolCalls != null ? "工具调用" : cr.content.Length + "字")
            + (r.Bool("recalled") ? " · 防撤回" : "");
        AfterReply(r, pb.text);
    }

    /* ================= 流式 ================= */

    private const int HOLD = 32;

    private void RunStream(string model, PromptBuilder.Result pb, HttpServer.Response res, HashSet<string> toolNames)
    {
        int timeout = Prefs.TimeoutSec;
        string id = OpenAiAdapter.NewId();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var gate = new object();
        int lastTh = 0;
        int emitted = 0;
        bool toolMode = false;
        bool finished = false;

        try { res.StartSse(); } catch (Exception) { return; }

        // 角色首帧
        WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, RoleDelta(), null));

        // 心跳保活
        var ping = new System.Threading.Timer(_ =>
        {
            lock (gate) { try { res.SseComment("ping"); } catch { } }
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        WebBridge.I.SendPromptStream(pb.text, null, timeout, new StreamListener(
            onDelta: (th, ct, recalled) =>
            {
                lock (gate)
                {
                    if (finished) return;
                    try
                    {
                        if (th.Length > lastTh)
                        {
                            string piece = th.Substring(lastTh);
                            lastTh = th.Length;
                            WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, Delta("reasoning_content", piece), null));
                        }
                        if (toolMode) { /* 工具块，静默缓冲，结束时统一下发 */ }
                        else
                        {
                            string norm = OpenAiAdapter.NormTag(ct);
                            int si = norm.IndexOf("<|tool_calls_begin|>", StringComparison.Ordinal);
                            if (si >= 0)
                            {
                                if (si > emitted)
                                    WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, Delta("content", ct.Substring(emitted, si - emitted)), null));
                                emitted = si;
                                toolMode = true;
                            }
                            else
                            {
                                int safe = ct.Length - HOLD;
                                if (safe > emitted)
                                {
                                    WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, Delta("content", ct.Substring(emitted, safe - emitted)), null));
                                    emitted = safe;
                                }
                            }
                        }
                    }
                    catch (Exception) { }
                }
            },
            onFinish: r =>
            {
                lock (gate)
                {
                    if (finished) return;
                    finished = true;
                    try { ping.Dispose(); } catch { }
                    try
                    {
                        string content = r.Str("content");
                        bool ok = r.Bool("ok") || content.Length > 0 || r.Str("thinking").Length > 0;
                        if (!ok)
                        {
                            WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, Delta("content", "\n[错误] " + r.Str("error", "UNKNOWN")), null));
                            WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, new JObj(), "stop"));
                            FinishSse(res, gate);
                            _lastCallInfo = "失败: " + r.Str("error");
                            return;
                        }
                        var cr = OpenAiAdapter.Process(r.Str("thinking"), content, toolNames);
                        if (cr.toolCalls != null && cr.toolCalls.Count > 0)
                        {
                            var tcs = OpenAiAdapter.ToOpenAiToolCalls(cr.toolCalls);
                            for (int i = 0; i < tcs.Count; i++)
                            {
                                var tc = tcs[i] as JObj;
                                var d = new JObj();
                                var arr = new JArr();
                                var item = new JObj();
                                try
                                {
                                    item.Set("index", (long)i);
                                    item.Set("id", tc.Str("id"));
                                    item.Set("type", "function");
                                    item.Set("function", tc.Obj("function"));
                                }
                                catch (Exception) { }
                                arr.Add(item);
                                d.Set("tool_calls", arr);
                                WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, d, null));
                            }
                            WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, new JObj(), "tool_calls"));
                            _lastCallInfo = "成功 · 工具调用 x" + tcs.Count;
                        }
                        else
                        {
                            if (toolMode)
                            {
                                // 出现过标记但并非有效工具调用（正文里在解释/举例）：把原文补发，绝不吞内容
                                if (content.Length > emitted)
                                    WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, Delta("content", content.Substring(emitted)), null));
                                Log.Write("检测到标记但非有效工具调用，已按正文输出");
                            }
                            else if (content.Length > emitted)
                            {
                                WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, Delta("content", content.Substring(emitted)), null));
                            }
                            WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, new JObj(), "stop"));
                            _lastCallInfo = "成功 · " + content.Length + "字" + (r.Bool("recalled") ? " · 防撤回" : "");
                        }
                        AfterReply(r, pb.text);
                        FinishSse(res, gate);
                    }
                    catch (Exception e)
                    {
                        Log.Write("流式收尾异常: " + e.Message);
                        try { FinishSse(res, gate); } catch { }
                    }
                }
            }));
    }

    private sealed class StreamListener : WebBridge.IStreamListener
    {
        private readonly Action<string, string, bool> _d;
        private readonly Action<JObj> _f;
        public StreamListener(Action<string, string, bool> onDelta, Action<JObj> onFinish) { _d = onDelta; _f = onFinish; }
        public void OnDelta(string t, string c, bool r) => _d(t, c, r);
        public void OnFinish(JObj r) => _f(r);
    }

    private void FinishSse(HttpServer.Response res, object gate)
    {
        lock (gate)
        {
            try { res.SseData("[DONE]"); } catch { }
            try { res.EndChunked(); } catch { }
            try { res.Close(); } catch { }
        }
    }

    private void WriteChunk(HttpServer.Response res, object gate, string json)
    {
        lock (gate) { try { res.SseData(json); } catch { } }
    }

    private static JObj RoleDelta()
    {
        var d = new JObj();
        d.Set("role", "assistant");
        d.Set("content", "");
        return d;
    }

    private static JObj Delta(string key, string value)
    {
        var d = new JObj();
        d.Set(key, value ?? "");
        return d;
    }

    private static string ErrJson(string msg, string type)
    {
        var err = new JObj();
        err.Set("message", msg ?? "");
        err.Set("type", type ?? "");
        err.Set("code", type ?? "");
        var e = new JObj();
        e.Set("error", err);
        return e.ToJson();
    }
}
