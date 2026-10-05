using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using DSWebApi.Desktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DSWebApi.Desktop;

/// <summary>
/// 真机端到端测试（--e2e &lt;手机号&gt; &lt;密码&gt;）：
/// 启动完整运行时（HTTP + WebView2 + bridge.js），自动登录官网，然后通过本地接口真实调用一次对话，
/// 最后把 /health、非流式与流式结果、页面截图写入 %LOCALAPPDATA%\DeepSeekWebAPI，退出码 0 表示全绿。
/// 说明：凭据只经内存传给注入脚本，不落盘、不打印。
/// </summary>
internal static class E2ETest
{
    private const string DsUrl = "https://chat.deepseek.com/";
    private static int _exit = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    public static int Run(string phone, string password)
    {
        try { AttachConsole(-1); } catch { }
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new E2EWindow(phone ?? "", password ?? ""));
        return _exit;
    }

    private sealed class E2EWindow : Form
    {
        private readonly string _phone;
        private readonly string _password;
        private readonly WebView2 _web;
        private CoreWebView2 _core;
        private string _shotPath = "";

        public E2EWindow(string phone, string password)
        {
            _phone = phone;
            _password = password;
            Text = "DeepSeek Web API · 端到端测试";
            Size = new Size(1280, 860);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(0x0D, 0x10, 0x15);
            ShowInTaskbar = true;
            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _ = Flow();
        }

        private static void P(string msg) => Log.Write("[E2E] " + msg);

        private static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…(" + s.Length + "字)");

        private static string Masked(string phone) =>
            string.IsNullOrEmpty(phone) || phone.Length < 7 ? "***" : phone.Substring(0, 3) + "****" + phone.Substring(phone.Length - 4);

        private async Task Flow()
        {
            int code = 1;
            try { code = await FlowCore(); }
            catch (Exception ex) { P("异常: " + ex); }
            P("===== 结束，exit=" + code + " =====");
            _exit = code;
            try { _core?.Stop(); } catch { }
            try { Close(); } catch { }
        }

        private async Task<int> FlowCore()
        {
            P("===== 开始（账号 " + Masked(_phone) + "）=====");
            Prefs.Load();
            if (_password.Length == 0) { P("FAIL 未提供密码"); return 3; }

            /* 1) 桥接脚本资源（本轮回归重点） */
            string bridge = Program.LoadEmbedded("bridge.js");
            string loginJs = Program.LoadEmbedded("e2e-login.js");
            if (bridge.Length < 5000) { P("FAIL bridge.js 未载入 bytes=" + bridge.Length); return 2; }
            WebBridge.I.BridgeJs = bridge;
            P("bridge.js=" + bridge.Length + " 字节 · e2e-login.js=" + loginJs.Length + " 字节");

            /* 2) HTTP 服务 */
            try { ChatEngine.I.StartAll(Prefs.Port); } catch (Exception ex) { P("HTTP 启动异常: " + ex.Message); }
            int port = ChatEngine.I.Port > 0 ? ChatEngine.I.Port : Prefs.Port;
            P("HTTP 端口=" + port + " · 局域网=" + ChatEngine.I.LanUrl);

            /* 3) WebView2 + 注入 */
            string ver;
            try { ver = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Exception ex) { P("FAIL 未检测到 WebView2 运行时: " + ex.Message); return 6; }
            P("WebView2 运行时=" + ver);

            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Log.Dir, "E2EWebView2"));
            await _web.EnsureCoreWebView2Async(env);
            _core = _web.CoreWebView2;
            try { _core.Settings.AreDevToolsEnabled = true; _core.Settings.IsStatusBarEnabled = false; } catch { }
            _core.WebMessageReceived += (s, ev) =>
            {
                try { WebBridge.I.OnWebMessage(ev.TryGetWebMessageAsString()); }
                catch (Exception ex) { P("消息异常: " + ex.Message); }
            };
            WebBridge.I.SetStatusListener(new EngineForwarder());

            var nav = new TaskCompletionSource<bool>();
            _core.NavigationCompleted += (s, ev) =>
            {
                WebBridge.I.MarkPageLoaded(ev.IsSuccess);
                if (ev.IsSuccess) { try { nav.TrySetResult(true); } catch { } }
            };
            await _core.AddScriptToExecuteOnDocumentCreatedAsync(WebBridge.ShimJs);
            WebBridge.I.Attach(_core);
            _core.Navigate(DsUrl);

            var finished = await Task.WhenAny(nav.Task, Task.Delay(90000));
            P("首页加载 " + (finished == nav.Task ? "成功" : "超时/失败"));
            await Task.Delay(5000);
            WebBridge.I.InjectBridge();
            await Task.Delay(2000);
            WebBridge.I.Probe();
            await Task.Delay(1500);

            var bridgeAlive = await Eval("(typeof window.DSKB === 'object' && window.DSKB) ? 'DSKB_OK' : 'DSKB_MISSING'");
            P("页面内 bridge 探测: " + Trunc(bridgeAlive, 200));
            if (!bridgeAlive.Contains("DSKB_OK")) { await Shot("e2e-bridge-missing.png"); P("FAIL bridge 未注入成功"); return 7; }

            /* 4) 登录 */
            var cred = new JObj();
            cred.Set("phone", _phone);
            cred.Set("password", _password);
            await Eval("window.__E2E_CRED__=" + cred.ToJson() + ";");

            for (int i = 0; i < 6 && !ChatEngine.I.LoggedIn; i++)
            {
                WebBridge.I.Probe();
                await Task.Delay(2500);
            }
            P("初始登录状态=" + ChatEngine.I.LoggedIn);

            if (!ChatEngine.I.LoggedIn)
            {
                for (int i = 0; i < 12 && !ChatEngine.I.LoggedIn; i++)
                {
                    string r = await Eval(loginJs);
                    P("登录 #" + i + ": " + Trunc(r, 1200));
                    if (r.Contains("\"captcha\":true") || r.Contains("\\\"captcha\\\":true"))
                    {
                        P("检测到验证码/滑块，移交人工处理");
                        break;
                    }
                    await Task.Delay(3000);
                    WebBridge.I.Probe();
                    await Task.Delay(1500);
                }
            }

            bool loggedIn = ChatEngine.I.LoggedIn;
            P("登录结果=" + loggedIn + " · url=" + _core.Source);
            await Shot(loggedIn ? "e2e-loggedin.png" : "e2e-fail.png");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(240) };
            try { P("GET /health -> " + Trunc(await http.GetStringAsync("http://127.0.0.1:" + port + "/health"), 600)); }
            catch (Exception ex) { P("health 失败: " + ex.Message); }

            if (!loggedIn) { P("FAIL 未登录，跳过对话测试"); return 4; }

            /* 5) 非流式真实调用 */
            string url = "http://127.0.0.1:" + port + "/v1/chat/completions";
            string resp = "";
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Prefs.ApiKey);
                req.Content = new StringContent(
                    "{\"model\":\"deepseek\",\"messages\":[{\"role\":\"user\",\"content\":\"只回复两个字：你好\"}],\"stream\":false}",
                    Encoding.UTF8, "application/json");
                var r = await http.SendAsync(req);
                resp = await r.Content.ReadAsStringAsync();
                P("非流式 HTTP " + (int)r.StatusCode + " -> " + Trunc(resp, 1200));
            }
            catch (Exception ex) { P("非流式调用异常: " + ex.Message); }

            string content = "";
            try
            {
                var o = Json.TryParse(resp) as JObj;
                var choices = o?.Arr("choices");
                if (choices != null && choices.Count > 0)
                    content = (choices[0] as JObj)?.Obj("message")?.Str("content", "") ?? "";
            }
            catch { }
            bool nonStreamOk = content.Trim().Length > 0;
            P("非流式正文长度=" + content.Length + (nonStreamOk ? " ✅" : " ❌"));

            /* 6) 流式调用 */
            int chunks = 0;
            var sb = new StringBuilder();
            try
            {
                var req2 = new HttpRequestMessage(HttpMethod.Post, url);
                req2.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Prefs.ApiKey);
                req2.Content = new StringContent(
                    "{\"model\":\"deepseek\",\"messages\":[{\"role\":\"user\",\"content\":\"只回复两个字：好的\"}],\"stream\":true}",
                    Encoding.UTF8, "application/json");
                var r2 = await http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead);
                P("流式 HTTP " + (int)r2.StatusCode);
                using var st = await r2.Content.ReadAsStreamAsync();
                using var rd = new StreamReader(st);
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < TimeSpan.FromSeconds(180))
                {
                    string line = await rd.ReadLineAsync();
                    if (line == null) break;
                    if (line.StartsWith("data:"))
                    {
                        chunks++;
                        if (sb.Length < 3000) sb.Append(line).Append('\n');
                        if (line.Contains("[DONE]")) break;
                    }
                }
            }
            catch (Exception ex) { P("流式调用异常: " + ex.Message); }
            P("流式分片=" + chunks + " -> " + Trunc(sb.ToString(), 500));
            bool streamOk = chunks > 0;

            P("---- 汇总: bridge=" + WebBridge.I.BridgeBytes + "B 登录=" + loggedIn
              + " 非流式=" + nonStreamOk + " 流式=" + streamOk + " 截图=" + _shotPath);

            if (loggedIn && nonStreamOk && streamOk) { P("E2E_OK"); return 0; }
            P("E2E_FAIL");
            return 5;
        }

        private sealed class EngineForwarder : WebBridge.IStatusListener
        {
            public void OnStatus(string json) { try { ChatEngine.I.OnStatus(json); } catch { } }
        }

        private async Task<string> Eval(string js)
        {
            if (_core == null) return "\"(no-core)\"";
            try { return await _core.ExecuteScriptAsync(js); }
            catch (Exception ex) { return "\"(eval-error:" + ex.Message.Replace("\"", "'") + ")\""; }
        }

        private async Task Shot(string name)
        {
            try
            {
                string p = Path.Combine(Log.Dir, name);
                using (var fs = File.Create(p)) await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                _shotPath = p;
                P("已截图: " + p);
            }
            catch (Exception ex) { P("截图失败: " + ex.Message); }
        }
    }
}
