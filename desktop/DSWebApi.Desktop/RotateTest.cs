using System.Net.Http;
using System.Text;
using DSWebApi.Desktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DSWebApi.Desktop;

/// <summary>
/// 多账号轮换真机测试（--e2e-rotate）：凭据来自环境变量 DS_TEST_ACCOUNTS（JSON 数组）。
/// 流程：为每个账号建独立 WebView2（独立数据目录）→ 自动登录 → 打开轮换 → 通过本地接口连发 N 次请求，
/// 验证「同一账号一次会话最多发 N 次就换号」「限流自动冷却」「失败不影响其他账号」。
/// 只打印账号序号与 device_id 前 8 位，绝不打印凭据。
/// </summary>
internal static class RotateTest
{
    private const string DsUrl = "https://chat.deepseek.com/";
    private static int _exit = 1;

    public static int Run(string accountsJson, int rounds)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new RotateWindow(accountsJson, rounds));
        return _exit;
    }

    private sealed class RotateWindow : Form
    {
        private readonly string _accountsJson;
        private readonly int _rounds;
        private readonly List<AccountSlot> _slots = new List<AccountSlot>();
        private readonly List<WebView2> _views = new List<WebView2>();
        private readonly List<CoreWebView2> _cores = new List<CoreWebView2>();
        private readonly string _runId = DateTime.Now.ToString("MMdd-HHmmss");

        public RotateWindow(string accountsJson, int rounds)
        {
            _accountsJson = accountsJson ?? "";
            _rounds = Math.Max(2, Math.Min(30, rounds));
            Text = "DeepSeek Web API · 多账号轮换测试";
            Size = new Size(1500, 900);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(0x0D, 0x10, 0x15);

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            Controls.Add(host);
        }

        private static void P(string msg) => Log.Write("[ROTATE] " + msg);

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _ = Flow();
        }

        private async Task Flow()
        {
            int code = 1;
            try { code = await FlowCore(); }
            catch (Exception ex) { P("异常: " + ex); }
            P("===== 轮换测试结束 exit=" + code + " =====");
            _exit = code;
            try { Close(); } catch { }
        }

        private async Task<int> FlowCore()
        {
            Prefs.Load();
            string bridge = Program.LoadEmbedded("bridge.js");
            string loginJs = Program.LoadEmbedded("e2e-login.js");
            if (bridge.Length < 5000 || loginJs.Length < 500) { P("FAIL 资源载入失败 bridge=" + bridge.Length + " login=" + loginJs.Length); return 2; }

            var creds = ParseAccounts(_accountsJson);
            if (creds.Count < 2) { P("FAIL 需要至少 2 个账号凭据，实际 " + creds.Count); return 3; }
            P("===== 轮换测试开始 · 账号数=" + creds.Count + " · 计划请求 " + _rounds + " 次 · 会话上限 " + Prefs.SessionSendLimit + " 次/账号 =====");

            /* 1) HTTP 服务 */
            try { ChatEngine.I.StartAll(Prefs.Port); } catch (Exception ex) { P("HTTP 启动异常: " + ex.Message); }
            int port = ChatEngine.I.Port > 0 ? ChatEngine.I.Port : Prefs.Port;
            P("HTTP 端口=" + port);

            /* 2) 每个账号一个独立 WebView2（独立数据目录，隔离彻底） */
            AccountPool.I.ClearForTest();
            for (int i = 0; i < creds.Count; i++)
            {
                var slot = new AccountSlot { Id = "e2e" + i, Name = "账号" + (i + 1), Enabled = true };
                var bridgeInst = new WebBridge { Name = slot.Name };
                bridgeInst.BridgeJs = bridge;
                slot.Bridge = bridgeInst;
                _slots.Add(slot);
                AccountPool.I.AttachAccount(slot);

                var wv = new WebView2 { Dock = DockStyle.Fill };
                _views.Add(wv);
                this.Controls.Add(wv);
                wv.BringToFront();

                try
                {
                    string udf = Path.Combine(Log.Dir, "e2e-rotate", _runId + "-a" + i);
                    Directory.CreateDirectory(udf);
                    var env = await CoreWebView2Environment.CreateAsync(null, udf);
                    await wv.EnsureCoreWebView2Async(env);
                    var core = wv.CoreWebView2;
                    _cores.Add(core);
                    try { core.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"; } catch { }
                    core.Settings.IsStatusBarEnabled = false;

                    var cap = slot;
                    core.WebMessageReceived += (s, ev) =>
                    {
                        try { cap.Bridge.OnWebMessage(ev.TryGetWebMessageAsString()); } catch { }
                    };
                    slot.Bridge.SetStatusListener(new SlotFwd(cap));
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(WebBridge.ShimJs);
                    slot.Bridge.Attach(core);

                    var nav = new TaskCompletionSource<bool>();
                    core.NavigationCompleted += (s, ev) => { slot.Bridge.MarkPageLoaded(ev.IsSuccess); if (ev.IsSuccess) nav.TrySetResult(true); };
                    core.Navigate(DsUrl);
                    var done = await Task.WhenAny(nav.Task, Task.Delay(90000));
                    P("账号" + (i + 1) + " 首页加载 " + (done == nav.Task ? "成功" : "超时"));
                    await Task.Delay(4000);
                    slot.Bridge.InjectBridge();
                    await Task.Delay(1500);

                    string probe = await core.ExecuteScriptAsync("(typeof window.DSKB === 'object' && window.DSKB) ? 'DSKB_OK' : 'MISSING'");
                    P("账号" + (i + 1) + " bridge 探测: " + probe);
                }
                catch (Exception ex) { P("账号" + (i + 1) + " 初始化失败: " + ex.Message); }
            }
            P("已创建 " + _slots.Count + " 个独立实例（各自独立数据目录与设备指纹）");

            /* 3) 逐号登录（串行，避免瞬时并发触发风控） */
            for (int i = 0; i < _slots.Count && i < creds.Count; i++)
            {
                var slot = _slots[i];
                var core = i < _cores.Count ? _cores[i] : null;
                if (core == null) continue;
                bool logged = await LoginOneAsync(slot, core, creds[i].Item1, creds[i].Item2, loginJs);
                P("账号" + (i + 1) + " 登录结果=" + logged);
                if (!logged)
                {
                    try
                    {
                        string st = await core.ExecuteScriptAsync("JSON.stringify({url:location.href,body:(document.body?document.body.innerText:'').replace(/\\s+/g,' ').slice(0,300)})");
                        P("账号" + (i + 1) + " 现场: " + Trunc(st, 500));
                    }
                    catch { }
                }
            }

            int usable = AccountPool.I.Available().Count;
            P("登录完成，可用账号 = " + usable + " / " + _slots.Count + "，" + AccountPool.I.SummaryText());
            if (usable < 2) { P("FAIL 可用账号不足 2 个，无法验证轮换"); return 4; }

            /* 4) 打开轮换，严格顺序连发：验证「账号1×N → 账号2×N → … → 一圈后全员新开对话」 */
            Prefs.RotateEnabled = true;
            int limit = Math.Max(1, Prefs.SessionSendLimit);
            var names = AccountPool.I.Snapshot().Where(x => x.LoggedIn && x.Enabled).Select(x => x.Name).ToList();
            int nAcc = names.Count;
            P("轮换已开启：顺序 = " + string.Join(" → ", names) + " · 每账号每对话连发 " + limit + " 次 · 冷却 " + Prefs.CooldownMinutes + " 分钟");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(240) };
            string url = "http://127.0.0.1:" + port + "/v1/chat/completions";
            int okCount = 0, rateLimited = 0, otherFail = 0, orderBad = 0, cycleBad = 0;
            int lastCycle = AccountPool.I.CycleNo;
            const string body = "{\"model\":\"deepseek\",\"messages\":[{\"role\":\"user\",\"content\":\"只回复两个字：好的\"}],\"stream\":false}";

            for (int r = 1; r <= _rounds; r++)
            {
                string expect = names[((r - 1) / limit) % nAcc];
                int expectCycle = 1 + (r - 1) / (limit * nAcc);
                var before = AccountPool.I.Snapshot().ToDictionary(x => x.Id, x => x.TotalCalls);
                string resp = ""; int status = 0;
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Prefs.ApiKey);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    var rr = await http.SendAsync(req);
                    status = (int)rr.StatusCode;
                    resp = await rr.Content.ReadAsStringAsync();
                }
                catch (Exception ex) { P("第 " + r + " 次请求异常: " + ex.Message); otherFail++; }

                // HTTP 响应在 SendJson 之后、OnSent 记账之前就回到客户端了，
                // 必须稍等再读快照，否则会误判成"没人承接"（曾导致顺序判定误报）。
                await Task.Delay(1000);
                string who = "?";
                foreach (var x in AccountPool.I.Snapshot())
                {
                    before.TryGetValue(x.Id, out var pre);
                    if (x.TotalCalls > pre) who = x.Name;
                }
                bool okOne = status == 200 && resp.Contains("\"content\"");
                if (okOne) okCount++;
                else if (status == 429 || resp.Contains("rate_limit")) rateLimited++;
                else otherFail++;

                bool orderOk = who == expect;
                if (!orderOk) orderBad++;
                int cyc = AccountPool.I.CycleNo;
                bool cycleOk = cyc == expectCycle;
                if (!cycleOk) cycleBad++;
                if (cyc > lastCycle) { P("  ↳ 所有账号都轮过一遍 → 进入第 " + cyc + " 轮（各账号应新开对话）"); lastCycle = cyc; }

                P("第 " + r + " 次 → 账号[" + who + "]（期望 " + expect + (orderOk ? " ✓" : " ✗") + "） 第 " + cyc + " 轮 HTTP " + status
                  + " · " + Trunc(resp.Replace("\n", " "), 120));
                P("    池状态: " + string.Join(" | ", AccountPool.I.Snapshot().Select(x =>
                      x.Name + "=" + x.StateText + "(总" + x.TotalCalls + ")")));
                await Task.Delay(150);
            }

            /* 5) 并发测试：同时发 nAcc 个请求，应分散到 nAcc 个不同账号 */
            P("---- 并发测试（同时发 " + nAcc + " 个请求）----");
            var pre2 = AccountPool.I.Snapshot().ToDictionary(x => x.Id, x => x.TotalCalls);
            int conOk = 0, con429 = 0, conFail = 0;
            var tasks = new List<Task>();
            for (int i = 0; i < nAcc; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var req = new HttpRequestMessage(HttpMethod.Post, url);
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Prefs.ApiKey);
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        var rr = await http.SendAsync(req);
                        string rp = await rr.Content.ReadAsStringAsync();
                        int st = (int)rr.StatusCode;
                        if (st == 200 && rp.Contains("\"content\"")) Interlocked.Increment(ref conOk);
                        else if (st == 429) Interlocked.Increment(ref con429);
                        else Interlocked.Increment(ref conFail);
                    }
                    catch { Interlocked.Increment(ref conFail); }
                }));
            }
            await Task.WhenAll(tasks);

            var hit = new Dictionary<string, int>();
            foreach (var x in AccountPool.I.Snapshot())
            {
                pre2.TryGetValue(x.Id, out var pre);
                int d = x.TotalCalls - pre;
                if (d > 0) hit[x.Name] = d;
            }
            bool conSpread = hit.Count >= Math.Min(nAcc, AccountPool.I.Available().Count);
            bool conLimitOk = hit.Values.All(v => v <= limit);
            P("并发落点: " + (hit.Count == 0 ? "无" : string.Join("、", hit.Select(kv => kv.Key + "×" + kv.Value)))
              + " · 成功 " + conOk + " / 429 " + con429 + " / 失败 " + conFail);
            P("并发分散到不同账号=" + conSpread + " · 单账号并发计数未越界=" + conLimitOk);

            /* 6) 结论 */
            P("---- 轮换结果 ----");
            foreach (var x in AccountPool.I.Snapshot())
                P(x.Name + " 承接 " + x.TotalCalls + " 次（成功 " + x.OkCalls + "，限流 " + x.RateLimitedCount
                  + "，当前对话已发 " + x.SentInSession + "/" + limit + "）状态=" + x.StateText);
            var served = AccountPool.I.Snapshot().Count(x => x.TotalCalls > 0);
            P("成功 " + okCount + " / 限流 " + rateLimited + " / 其他失败 " + otherFail + " · 参与账号 " + served + " 个");
            bool rotated = served >= Math.Min(2, usable);
            bool limitOk = AccountPool.I.Snapshot().All(x => x.TotalCalls == 0 || x.SentInSession <= limit);
            bool orderOkAll = orderBad == 0 || rateLimited > 0;
            bool cycleOkAll = cycleBad == 0 || rateLimited > 0;
            P("轮换生效=" + rotated + " · 顺序正确=" + orderOkAll + "（错 " + orderBad + " 次）· 轮次推进正确=" + cycleOkAll
              + "（错 " + cycleBad + " 次）· 会话上限未越界=" + limitOk);
            P(conSpread ? "CONCUR_OK" : "CONCUR_FAIL");
            if (okCount > 0 && rotated && limitOk && orderOkAll && cycleOkAll && conSpread) { P("ROTATE_OK"); return 0; }
            P("ROTATE_FAIL");
            return 5;
        }

        private sealed class SlotFwd : WebBridge.IStatusListener
        {
            private readonly AccountSlot _s;
            public SlotFwd(AccountSlot s) { _s = s; }
            public void OnStatus(string json) { try { ChatEngine.I.OnSlotStatus(_s, json); } catch { } }
        }

        private async Task<bool> LoginOneAsync(AccountSlot slot, CoreWebView2 core, string user, string pass, string loginJs)
        {
            try
            {
                var cred = new JObj();
                cred.Set("phone", user);
                cred.Set("password", pass);
                await core.ExecuteScriptAsync("window.__E2E_CRED__=" + cred.ToJson() + ";");

                for (int i = 0; i < 6 && !slot.LoggedIn; i++)
                {
                    slot.Bridge.Probe();
                    await Task.Delay(2500);
                }
                if (slot.LoggedIn) return true;

                for (int i = 0; i < 14 && !slot.LoggedIn; i++)
                {
                    string r = await core.ExecuteScriptAsync(loginJs);
                    if (r.Contains("captcha\\\":true") || r.Contains("\"captcha\":true")) { P(slot.Name + " 出现验证码，跳过自动登录"); break; }
                    await Task.Delay(3000);
                    slot.Bridge.Probe();
                    await Task.Delay(1200);
                }
                return slot.LoggedIn;
            }
            catch (Exception ex) { P(slot.Name + " 登录异常: " + ex.Message); return false; }
        }

        private static List<(string, string)> ParseAccounts(string json)
        {
            var list = new List<(string, string)>();
            try
            {
                var arr = Json.TryParse(json) as JArr;
                if (arr == null) return list;
                for (int i = 0; i < arr.Count; i++)
                {
                    var o = arr[i] as JObj;
                    if (o == null) continue;
                    string u = o.Str("phone", o.Str("user", o.Str("email", "")));
                    string p = o.Str("password", "");
                    if (u.Length > 0 && p.Length > 0) list.Add((u, p));
                }
            }
            catch (Exception e) { P("解析账号失败: " + e.Message); }
            return list;
        }

        private static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");
    }
}
