using Microsoft.Web.WebView2.Core;

namespace DSWebApi.Desktop.Core;

/// <summary>
/// 原生侧与注入脚本 bridge.js 的唯一通道：
/// 在 UI 线程执行 JS，用 reqId + TaskCompletionSource 取回结果，并支持流式回调。
/// （对应 APK 的 DeepSeekController）
/// </summary>
public sealed class WebBridge
{
    public interface IStatusListener { void OnStatus(string json); }

    public interface IStreamListener
    {
        /// <summary>content/thinking 为累计全文，调用方自行做增量差分。</summary>
        void OnDelta(string thinking, string content, bool recalled);
        void OnFinish(JObj result);
    }

    private static readonly WebBridge _inst = new WebBridge();
    public static WebBridge I => _inst;

    private readonly object _lk = new object();
    private volatile CoreWebView2 _wv;
    private readonly Dictionary<string, TaskCompletionSource<JObj>> _pending = new Dictionary<string, TaskCompletionSource<JObj>>();
    private readonly Dictionary<string, IStreamListener> _streams = new Dictionary<string, IStreamListener>();
    private SynchronizationContext _ui;
    private volatile IStatusListener _listener;
    private string _bridgeJs = "";

    /// <summary>所属账号名（日志用，多账号轮换时区分）。</summary>
    public string Name = "";

    public string BridgeJs { get => _bridgeJs; set => _bridgeJs = value ?? ""; }
    public void SetStatusListener(IStatusListener l) => _listener = l;
    public bool IsAttached => _wv != null;
    public bool IsReady => _wv != null && _pageLoaded;

    /// <summary>bridge.js 是否真的载入（0 字节 = 所有调用必失败，需在健康检查里暴露出来）。</summary>
    public int BridgeBytes => _bridgeJs == null ? 0 : _bridgeJs.Length;
    public bool BridgeLoaded => BridgeBytes >= 200;
    private volatile bool _pageLoaded;

    public void MarkPageLoaded(bool v) => _pageLoaded = v;

    /// <summary>注入到每个文档开头的 shim：把 bridge.js 的 window.DSB.onEvent 转发到 WebView2 消息通道。</summary>
    public const string ShimJs =
        "if(!window.DSB){window.DSB={onEvent:function(s){try{window.chrome.webview.postMessage(String(s));}catch(e){}}};}" +
        "if(!window.__DSWB_SHIM__){window.__DSWB_SHIM__=1;}";

    public void Attach(CoreWebView2 wv)
    {
        _ui = SynchronizationContext.Current;
        _wv = wv;
    }

    public void Detach()
    {
        _wv = null;
        _pageLoaded = false;
        FailAll("WEBVIEW_DETACHED");
    }

    public bool IsBusy
    {
        get { lock (_lk) return _pending.Count > 0 || _streams.Count > 0; }
    }

    private void FailAll(string err)
    {
        List<TaskCompletionSource<JObj>> ps;
        List<IStreamListener> sl;
        lock (_lk)
        {
            ps = new List<TaskCompletionSource<JObj>>(_pending.Values);
            sl = new List<IStreamListener>(_streams.Values);
            _pending.Clear();
            _streams.Clear();
        }
        foreach (var p in ps) p.TrySetResult(Err(err));
        foreach (var s in sl) { try { s.OnFinish(Err(err)); } catch { } }
    }

    /* ================= JS 注入 ================= */

    private void Post(Action act)
    {
        var ctx = _ui;
        if (ctx != null) ctx.Post(_ => { Safe(act); }, null);
        else Safe(act);
    }

    private static void Safe(Action a)
    {
        try { a(); } catch (Exception e) { Log.Write("JS 执行失败: " + e.Message); }
    }

    /// <summary>注入 shim + bridge.js（幂等）。</summary>
    public void InjectBridge()
    {
        var wv = _wv;
        if (wv == null) return;
        Post(() =>
        {
            try { _ = wv.ExecuteScriptAsync(ShimJs); } catch (Exception e) { Log.Write("注入 shim 失败: " + e.Message); }
            if (!string.IsNullOrEmpty(_bridgeJs))
            {
                try { _ = wv.ExecuteScriptAsync(_bridgeJs); } catch (Exception e) { Log.Write("注入 bridge.js 失败: " + e.Message); }
            }
        });
    }

    private void Eval(string js)
    {
        if (string.IsNullOrEmpty(js)) return;
        var wv = _wv;
        if (wv == null) return;
        Post(() =>
        {
            try { _ = wv.ExecuteScriptAsync(js); } catch (Exception e) { Log.Write("eval 失败: " + e.Message); }
        });
    }

    public void Probe() => Eval("window.DSKB && DSKB.probe();");

    public void Configure(string thinking, string search)
    {
        var o = new JObj();
        o.Set("thinking", thinking ?? "auto");
        o.Set("search", search ?? "auto");
        Eval("window.DSKB && DSKB.configure(" + o.ToJson() + ");");
    }

    public void SetTheme(bool dark) => Eval("window.DSKB && DSKB.setTheme(" + (dark ? "true" : "false") + ");");

    public void RequestSessionId() => Eval("window.DSKB && window.DSB && DSB.onEvent(JSON.stringify({type:'session',sessionId:window.DSKB.sessionId?window.DSKB.sessionId():''}));");

    /* ================= 调用与事件 ================= */

    private static string NewReqId() => Guid.NewGuid().ToString("N").Substring(0, 16);

    private static JObj Err(string msg)
    {
        var o = new JObj();
        o.Set("ok", false);
        o.Set("error", msg ?? "");
        return o;
    }

    /// <summary>同步调用 bridge.js 的某个方法并等待结果。</summary>
    private JObj CallJs(string method, JObj arg, int timeoutSec)
    {
        var wv = _wv;
        if (wv == null) return Err("APP_NOT_RUNNING:请保持本程序运行且已登录 DeepSeek 官网");
        try
        {
            string reqId = NewReqId();
            arg.Set("reqId", reqId);
            var tcs = new TaskCompletionSource<JObj>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lk) _pending[reqId] = tcs;

            string fallback = "(function(){try{DSB.onEvent(JSON.stringify({type:'" + method + "',reqId:'" + reqId +
                              "',ok:false,error:'NO_BRIDGE'}))}catch(e){}})();";
            Eval("window.DSKB ? DSKB." + method + "(" + arg.ToJson() + ") : " + fallback);

            if (tcs.Task.Wait(TimeSpan.FromSeconds(Math.Max(1, timeoutSec))))
            {
                lock (_lk) _pending.Remove(reqId);
                var r = tcs.Task.Result;
                return r ?? Err("NULL");
            }
            lock (_lk) _pending.Remove(reqId);
            return Err("TIMEOUT");
        }
        catch (Exception e)
        {
            return Err(e.Message);
        }
    }

    /// <summary>非流式发送，阻塞等待完整回复。</summary>
    public JObj SendPrompt(string prompt, string sessionId, int timeoutSec)
    {
        var arg = new JObj();
        arg.Set("prompt", prompt ?? "");
        if (!string.IsNullOrEmpty(sessionId)) arg.Set("sessionId", sessionId);
        return CallJs("send", arg, timeoutSec);
    }

    /// <summary>流式发送：立即返回 reqId，事件通过 listener 异步回传。</summary>
    public string SendPromptStream(string prompt, string sessionId, int timeoutSec, IStreamListener l)
    {
        var wv = _wv;
        if (wv == null)
        {
            l?.OnFinish(Err("APP_NOT_RUNNING:请保持本程序运行且已登录 DeepSeek 官网"));
            return null;
        }
        string reqId = NewReqId();
        lock (_lk) _streams[reqId] = l;

        var arg = new JObj();
        arg.Set("prompt", prompt ?? "");
        arg.Set("reqId", reqId);
        if (!string.IsNullOrEmpty(sessionId)) arg.Set("sessionId", sessionId);
        Eval("window.DSKB ? DSKB.send(" + arg.ToJson() + ") : (function(){try{DSB.onEvent(JSON.stringify({type:'reply',reqId:'" +
             reqId + "',ok:false,error:'NO_BRIDGE'}))}catch(e){}})();");

        var timer = new System.Threading.Timer(_ =>
        {
            IStreamListener sl = null;
            lock (_lk) { if (_streams.Remove(reqId, out var s)) sl = s; }
            if (sl != null) { try { sl.OnFinish(Err("TIMEOUT")); } catch { } }
        }, null, TimeSpan.FromSeconds(timeoutSec + 5), Timeout.InfiniteTimeSpan);

        lock (_lk) _timers[reqId] = timer;
        return reqId;
    }

    private readonly Dictionary<string, System.Threading.Timer> _timers = new Dictionary<string, System.Threading.Timer>();

    /// <summary>上传附件（base64）到官网输入区，阻塞等待上传/解析完成。</summary>
    public JObj AttachFile(string name, string mime, string base64, int timeoutSec)
    {
        var arg = new JObj();
        arg.Set("name", name ?? "");
        arg.Set("mime", mime ?? "");
        arg.Set("b64", base64 ?? "");
        return CallJs("attachFile", arg, timeoutSec);
    }

    public JObj NewChat() => CallJs("newChat", new JObj(), 20);

    /// <summary>接收 bridge.js 事件（必须从 UI 线程调用）。</summary>
    public void OnWebMessage(string json)
    {
        JObj o;
        try { o = Json.ParseStrict(json) as JObj; }
        catch (Exception e) { Log.Write("onJsEvent 解析失败: " + e.Message); return; }
        if (o == null) return;

        string type = o.Str("type");
        string reqId = o.Str("reqId", "");

        if (type == "delta")
        {
            IStreamListener sl = null;
            if (reqId.Length > 0) lock (_lk) _streams.TryGetValue(reqId, out sl);
            if (sl != null)
            {
                try { sl.OnDelta(o.Str("thinking"), o.Str("content"), o.Bool("recalled")); } catch { }
            }
            return;
        }

        TaskCompletionSource<JObj> p = null;
        IStreamListener ls = null;
        if (reqId.Length > 0)
        {
            lock (_lk)
            {
                if (_pending.Remove(reqId, out var pp)) p = pp;
                if (_streams.Remove(reqId, out var ss)) ls = ss;
                if (_timers.Remove(reqId, out var tm)) { try { tm.Dispose(); } catch { } }
            }
        }
        p?.TrySetResult(o);
        if (ls != null && type == "reply")
        {
            try { ls.OnFinish(o); } catch { }
        }

        switch (type)
        {
            case "reply":
                Log.Write("回复 via=" + o.Str("via", "dom") + " ok=" + o.Bool("ok")
                    + (o.Bool("recalled") ? " [撤回已拦截]" : "")
                    + " 正文=" + o.Str("content").Length + "字 思考=" + o.Str("thinking").Length + "字"
                    + (o.Str("error").Length == 0 ? "" : " err=" + o.Str("error")));
                return;
            case "attach":
                Log.Write("附件挂载 " + (o.Bool("ok") ? "成功" : "失败") + " " + o.Str("name", "")
                    + (o.Bool("ok") ? "" : " err=" + o.Str("error")));
                return;
            case "newChat":
                Log.Write("新建对话: " + (o.Bool("ok") ? "成功" : "失败"));
                return;
            case "pageReply":
                Log.Write("页面手动回复 正文=" + o.Str("content").Length + "字"
                    + (o.Bool("recalled") ? " [撤回已拦截]" : ""));
                return;
            case "probe":
            case "boot":
            case "session":
                break;
        }

        var l = _listener;
        if (l != null) { try { l.OnStatus(o.ToJson()); } catch { } }
    }
}
