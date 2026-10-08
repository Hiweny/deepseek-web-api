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

    /* ---- 多账号轮换：单次发送的结果（队列串行执行，无需并发保护） ---- */
    private string _sendErr = null;
    private string _sendSessionId = "";
    private bool _sendOk;

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
        if (!WebBridge.I.BridgeLoaded) _state = "运行中（桥接脚本未载入）";
        else if (!WebBridge.I.IsReady) _state = "运行中（页面加载中）";
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

    /// <summary>单个账号的 bridge 事件（多账号时每个账号各有一条）。</summary>
    public void OnSlotStatus(AccountSlot slot, string json)
    {
        if (slot == null || string.IsNullOrEmpty(json)) return;
        try
        {
            var o = Json.TryParse(json) as JObj;
            if (o == null) return;
            string type = o.Str("type");
            if (type == "probe" || type == "boot")
            {
                slot.Probed = true;
                if (o.Has("loggedIn")) slot.LoggedIn = o.Bool("loggedIn");
                if (o.Has("ready")) slot.PageReady = o.Bool("ready");
                string sid = o.Str("sessionId", "");
                if (sid.Length > 0) slot.SessionId = sid;
            }
            else if (type == "session")
            {
                slot.SessionId = o.Str("sessionId", "");
            }
            else if (type == "newChat")
            {
                slot.SentInSession = 0;
                slot.NeedNewChat = false;
                string sid2 = o.Str("sessionId", "");
                if (sid2.Length > 0) { slot.SessionId = sid2; slot.TrackedSessionId = sid2; }
            }
            else if (type == "contextLimit")
            {
                if (slot != null) slot.NeedNewChat = true;
                Log.Write("账号 " + (slot != null ? slot.Name : "?") + " 页面提示上下文超限：" + o.Str("message", ""));
            }
            else if (type == "rateLimited")
            {
                AccountPool.I.MarkCooldown(slot, Prefs.CooldownMinutes, o.Str("message", "消息发送频繁"));
            }
        }
        catch { }
        Notify();
    }

    /// <summary>错误里带「上下文超限」标记。</summary>
    public static bool LookContextLimit(string err)
    {
        if (string.IsNullOrEmpty(err)) return false;
        if (err.IndexOf("CONTEXT_LIMIT", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return (err.Contains("上下文") && (err.Contains("上限") || err.Contains("超出") || err.Contains("过长") || err.Contains("达到")))
            || err.Contains("长度上限") || err.Contains("达到最大长度");
    }

    /// <summary>官网把「上下文超限，请开启新对话」当成一条普通回复返回时的识别（严格限长，避免误判正常回答）。</summary>
    public static bool LookContextLimitText(string content, string thinking)
    {
        if (string.IsNullOrEmpty(content)) return false;
        if (!string.IsNullOrEmpty(thinking)) return false;      // 有思考内容 = 正常回答
        string c = content.Trim();
        if (c.Length > 120) return false;
        if (LookContextLimit(c)) return true;
        return c.Contains("请开启新对话") || c.Contains("请新建对话") || c.Contains("开启新对话后");
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
    private void AfterReply(AccountSlot slot, JObj r, string promptText)
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
                if (slot != null)
                {
                    if (sid.Length > 0 && sid != slot.TrackedSessionId) { slot.TrackedSessionId = sid; slot.SessionTokens = 0; }
                    slot.SessionTokens += promptTok + replyTok;
                    used = slot.SessionTokens;
                }
                else
                {
                    if (sid.Length > 0 && sid != _trackedSessionId) { _trackedSessionId = sid; _sessionTokens = 0; }
                    _sessionTokens += promptTok + replyTok;
                    used = _sessionTokens;
                }
                limit = ContextLimitTokens();
                pct = NewChatThresholdPct();
                long threshold = limit * pct / 100;
                if (Prefs.AutoNewChat && threshold > 0 && used >= threshold)
                {
                    rotate = true;
                    if (slot != null) { slot.SessionTokens = 0; slot.TrackedSessionId = ""; slot.NeedNewChat = true; }
                    else { _sessionTokens = 0; _trackedSessionId = ""; }
                }
                _contextInfo = (slot != null ? slot.Name + " " : "") + FmtTokens(used) + " / " + FmtTokens(limit)
                    + "（" + (limit > 0 ? used * 100 / limit : 0) + "%）";
            }
            Log.Write("会话上下文 ≈ " + used + " tokens / " + limit + "（阈值 " + pct + "%）"
                + (rotate ? " → 达阈值，下次该账号将新开对话" : ""));
            if (rotate && slot == null) RotateSession();
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
        o.Set("bridge_loaded", WebBridge.I.BridgeLoaded);
        o.Set("bridge_bytes", (long)WebBridge.I.BridgeBytes);
        o.Set("logged_in", _lastProbeLoggedIn);
        o.Set("inflight", (long)Inflight);
        o.Set("total_calls", (long)TotalCalls);
        o.Set("ok_calls", (long)OkCalls);
        o.Set("port", (long)Port);
        o.Set("rotate_enabled", Prefs.RotateEnabled);
        o.Set("accounts_summary", AccountPool.I.SummaryText());
        o.Set("accounts", (long)AccountPool.I.Snapshot().Count);
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

        // ---- 选账号：轮换开启时从账号池挑一个；关闭时就是原来的单账号路径 ----
        bool rotate = Prefs.RotateEnabled && AccountPool.I.AnyUsable;
        AccountSlot slot = null;
        bool needNewChat = false;
        WebBridge wb = WebBridge.I;
        if (rotate)
        {
            slot = AccountPool.I.Acquire(out needNewChat);
            if (slot == null || slot.Bridge == null)
            {
                int wait = AccountPool.I.MinCooldownLeftSec();
                Log.Write("所有账号都在冷却/不可用，拒绝请求（等 " + wait + "s）");
                res.SendJson(429, ErrJson("所有账号都在冷却中（消息发送频繁），请在 " + AccountSlot.FmtLeft(wait) + " 后重试。",
                    "rate_limit_error"));
                return;
            }
            wb = slot.Bridge;
        }
        else
        {
            if (!WebBridge.I.IsAttached)
            {
                res.SendJson(503, ErrJson("网页桥未就绪：请保持本程序运行并在「对话页」登录 DeepSeek 官网。", "server_error"));
                return;
            }
        }
        if (!wb.BridgeLoaded)
        {
            res.SendJson(503, ErrJson("桥接脚本 bridge.js 未载入（内嵌资源异常）：请确认安装包完整，或把 bridge.js 放到 exe 同目录的 Assets\\ 下后重启。", "server_error"));
            return;
        }

        string model = body.Str("model", "deepseek");
        bool stream = body.Bool("stream", false);
        bool stateless = Prefs.Stateless;

        PromptBuilder.Result pb;
        try { pb = PromptBuilder.Build(body, stateless); }
        catch (Exception e) { res.SendJson(400, ErrJson("Bad request: " + e.Message, "invalid_request_error")); return; }

        if (pb.toolNames.Count > 0) _lastToolNames = new HashSet<string>(pb.toolNames, StringComparer.OrdinalIgnoreCase);
        // 工具声明的 JSON Schema（用于按类型归一化参数：声明为 string 的参数不能传对象/数组）
        var toolSchema = ToolArgsFixer.BuildIndex(body.Arr("tools"));
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
        wb.Configure(thinking, search);

        Interlocked.Increment(ref _inflight);
        Interlocked.Increment(ref _totalCalls);
        long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Notify();

        _queue.Add(() =>
        {
            bool slotDone = false;
            try
            {
                _sendErr = null; _sendSessionId = ""; _sendOk = false;
                if (slot != null)
                {
                    long ctxLimit = ContextLimitTokens() * NewChatThresholdPct() / 100;
                    if (!needNewChat && Prefs.AutoNewChat && ctxLimit > 0 && slot.SessionTokens >= ctxLimit)
                    {
                        needNewChat = true;
                        Log.Write("账号 " + slot.Name + " 对话上下文已到阈值（≈" + slot.SessionTokens + " token）→ 先新开对话");
                    }
                    if (needNewChat)
                    {
                        Log.Write("账号 " + slot.Name + " 新开对话（轮次切换 / 上下文已满）");
                        var nc = slot.Bridge.NewChat();
                        AccountPool.I.MarkChatOpened(slot, nc.Str("sessionId"));
                        System.Threading.Thread.Sleep(400);
                    }
                }
                // 附件：逐个注入官网，等待上传/解析
                foreach (var a in pb.attachments)
                {
                    var r = wb.AttachFile(a.Name, a.Mime, a.Base64, 150);
                    if (!r.Bool("ok")) Log.Write("附件挂载失败: " + a.Name + " " + r.Str("error"));
                }
                if (stream) RunStream(model, pb, res, toolNames, wb, slot, toolSchema);
                else RunBlocking(model, pb, res, toolNames, wb, slot, toolSchema);
                if (slot != null)
                {
                    slotDone = true;
                    AccountPool.I.OnSent(slot, _sendOk, _sendOk ? null : _sendErr, _sendSessionId);
                    if (!_sendOk) Log.Write("账号 " + slot.Name + " 本次失败: " + _sendErr
                        + (AccountPool.LooksRateLimited(_sendErr) ? "（已标记限流并冷却）" : ""));
                }
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
                if (slot != null && !slotDone)
                {
                    // 异常路径：归还预占名额，避免账号被"永久占用"
                    try { AccountPool.I.OnSent(slot, false, "EXCEPTION", ""); } catch { }
                }
                if (!stream) { try { res.Close(); } catch { } }
                Interlocked.Decrement(ref _inflight);
                Notify();
            }
        });
    }

    private void RunBlocking(string model, PromptBuilder.Result pb, HttpServer.Response res, HashSet<string> toolNames, WebBridge wb, AccountSlot slot,
        Dictionary<string, ToolArgsFixer.Schema> toolSchema)
    {
        int timeout = Prefs.TimeoutSec;
        var r = wb.SendPrompt(pb.text, null, timeout);
        _sendSessionId = r.Str("sessionId", "");
        string thinking = r.Str("thinking");
        string content = r.Str("content");
        string err0 = r.Str("error", "");

        // 上下文超限（官网 toast，或官网把超限提示当成一条正文返回）→ 自动新开对话并重试一次。
        // 此时还没写任何响应，重试是安全的。
        if (slot != null && (LookContextLimit(err0) || LookContextLimitText(content, thinking)))
        {
            Log.Write("账号 " + slot.Name + " 命中上下文上限 → 自动新开对话并重试（" + (err0.Length > 0 ? err0 : content) + "）");
            var nc = slot.Bridge.NewChat();
            AccountPool.I.MarkChatOpened(slot, nc.Str("sessionId"));
            System.Threading.Thread.Sleep(500);
            r = wb.SendPrompt(pb.text, null, timeout);
            _sendSessionId = r.Str("sessionId", "");
            thinking = r.Str("thinking");
            content = r.Str("content");
        }

        if (content.Length == 0 && thinking.Length == 0)
        {
            string err = r.Str("error", "EMPTY");
            _sendOk = false; _sendErr = err;
            res.SendJson(502, ErrJson("DeepSeek 未返回内容: " + err, "server_error"));
            _lastCallInfo = "失败: " + err;
            return;
        }
        var cr = OpenAiAdapter.Process(thinking, content, toolNames, toolSchema);
        string id = OpenAiAdapter.NewId();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var outObj = OpenAiAdapter.BuildCompletion(id, model, created, cr.thinking, cr.content, cr.toolCalls,
            cr.finishReason, OpenAiAdapter.EstTokens(pb.text), OpenAiAdapter.EstTokens(cr.content + cr.thinking));
        _sendOk = true; _sendErr = null;
        res.SendJson(outObj.ToJson());
        _lastCallInfo = "成功 · " + (cr.toolCalls != null ? "工具调用" : cr.content.Length + "字")
            + (r.Bool("recalled") ? " · 防撤回" : "") + (slot != null ? " · " + slot.Name : "");
        AfterReply(slot, r, pb.text);
    }

    /* ================= 流式 ================= */

    private const int HOLD = 32;

    private void RunStream(string model, PromptBuilder.Result pb, HttpServer.Response res, HashSet<string> toolNames, WebBridge wb, AccountSlot slot,
        Dictionary<string, ToolArgsFixer.Schema> toolSchema)
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

        wb.SendPromptStream(pb.text, null, timeout, new StreamListener(
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
                            // 只要出现工具标记信号（各种宽窄变体、乃至孤立残片都算），
                            // 就停在标记之前转入静默 —— 绝不让标记或残片上屏。
                            // 旧实现只认精确的 <|tool_calls_begin|>，且残片会随 HOLD 尾部一起漏出。
                            int safe = ToolMarkup.SafeEmitEnd(ct);
                            if (safe > emitted)
                            {
                                WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, Delta("content", ct.Substring(emitted, safe - emitted)), null));
                                emitted = safe;
                            }
                            if (ToolMarkup.EarliestSignal(ct) >= 0) toolMode = true;
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
                            _sendOk = false; _sendErr = r.Str("error", "UNKNOWN"); _sendSessionId = r.Str("sessionId", "");
                            _lastCallInfo = "失败: " + r.Str("error");
                            return;
                        }
                        var cr = OpenAiAdapter.Process(r.Str("thinking"), content, toolNames, toolSchema);
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
                            // 非有效工具调用：补发剩余正文。**必须先剥掉标记残片**，
                            // 否则「静默期之后」的残片会在这里逃逸成正文（用户实测的 bug）。
                            string tailText = content.Length > emitted ? content.Substring(emitted) : "";
                            tailText = ToolMarkup.StripStray(tailText);
                            if (tailText.Length > 0)
                                WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, Delta("content", tailText), null));
                            if (toolMode) Log.Write("检测到标记但非有效工具调用，已按正文输出（残片已剥离）");
                            WriteChunk(res, gate, OpenAiAdapter.Chunk(id, model, created, new JObj(), "stop"));
                            _lastCallInfo = "成功 · " + content.Length + "字" + (r.Bool("recalled") ? " · 防撤回" : "");
                        }
                        _sendOk = true; _sendErr = null; _sendSessionId = r.Str("sessionId", "");
                        AfterReply(slot, r, pb.text);
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
