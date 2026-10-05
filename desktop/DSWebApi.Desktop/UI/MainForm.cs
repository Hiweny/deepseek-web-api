using System.Diagnostics;
using System.Drawing.Drawing2D;
using DSWebApi.Desktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DSWebApi.Desktop.UI;

/// <summary>桌面主窗口：侧边导航 + 控制台 / 对话页 / 设置 / 日志；常驻托盘、看门狗保活。</summary>
public sealed class MainForm : Form
{
    /* ---------------- 配色 ---------------- */
    private static readonly Color CBg = Color.FromArgb(0x0E, 0x11, 0x16);
    private static readonly Color CSide = Color.FromArgb(0x12, 0x16, 0x1D);
    private static readonly Color CCard = Color.FromArgb(0x18, 0x1D, 0x26);
    private static readonly Color CLine = Color.FromArgb(0x27, 0x2E, 0x3A);
    private static readonly Color CText = Color.FromArgb(0xE7, 0xEA, 0xF0);
    private static readonly Color CSub = Color.FromArgb(0x98, 0xA2, 0xB2);
    private static readonly Color CAccent = Color.FromArgb(0x5A, 0x94, 0xF8);
    private static readonly Color CAccentDim = Color.FromArgb(0x1C, 0x27, 0x3D);

    private const string DsUrl = "https://chat.deepseek.com/";
    private const string WebView2Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly bool _startMinimized;
    private readonly List<Button> _navBtns = new List<Button>();
    private readonly List<Control> _pages = new List<Control>();
    private readonly Dictionary<Panel, int> _cardHeights = new Dictionary<Panel, int>();
    private int _page;
    private bool _reallyExit;

    private FlowLayoutPanel _consoleFlow;
    private Panel _webHost;
    private Label _webHint;
    private WebView2 _web;
    private bool _webTried;
    private bool _balloonShown;

    private Label _vState, _vWeb, _vLogin, _vCalls, _vCtx, _vLast, _vBase, _vLan, _vKey;
    private Label _sideStatus;
    private TextBox _logBox;

    private NotifyIcon _tray;
    private ToolStripMenuItem _trayState;
    private System.Windows.Forms.Timer _timer;
    private int _tick;
    private EventWaitHandle _showEvent;
    private Thread _showWatcher;

    public MainForm(bool startMinimized)
    {
        _startMinimized = startMinimized;
        Text = Program.AppName + " v" + Program.Version + " · Windows";
        BackColor = CBg;
        ForeColor = CText;
        Font = new Font("Microsoft YaHei UI", 9F);
        MinimumSize = new Size(980, 620);
        Size = new Size(1220, 780);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        try { var ico = Icon.ExtractAssociatedIcon(Environment.ProcessPath); if (ico != null) Icon = ico; } catch { }

        BuildUi();
        BuildTray();
        StartSingleInstanceWatcher();

        ChatEngine.I.Changed += () =>
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(RefreshStats)); } catch { }
        };
        WebBridge.I.SetStatusListener(new StatusForwarder(this));

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += OnTick;
        _timer.Start();

        Shown += (s, e) =>
        {
            Log.Write("界面已显示");
            ChatEngine.I.StartAll(Prefs.Port);
            RefreshStats();
            _ = InitWebAsync();
            if (_startMinimized) MinimizeToTray(true);
        };
    }

    /* ================= 布局 ================= */

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = CBg,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 214));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var side = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = CSide,
            Margin = Padding.Empty,
        };
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.Controls.Add(side, 0, 0);

        // 标题
        var title = new Panel { Dock = DockStyle.Fill, BackColor = CSide };
        side.Controls.Add(title, 0, 0);
        try
        {
            var ico = Icon;
            if (ico != null)
            {
                var pic = new PictureBox { Left = 18, Top = 22, Width = 30, Height = 30, SizeMode = PictureBoxSizeMode.Zoom, Image = ico.ToBitmap() };
                title.Controls.Add(pic);
            }
        }
        catch { }        title.Controls.Add(new Label
        {
            Text = "DeepSeek Web API", Left = 56, Top = 20, AutoSize = true,
            ForeColor = CText, Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
        });
        title.Controls.Add(new Label
        {
            Text = "Windows 桌面版 v" + Program.Version, Left = 56, Top = 46, AutoSize = true,
            ForeColor = CSub, Font = new Font("Microsoft YaHei UI", 8F),
        });

        // 导航
        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = CSide, Padding = new Padding(10, 4, 10, 4),
        };
        side.Controls.Add(nav, 0, 1);
        AddNav(nav, "\uD83C\uDF9B   控制台", 0);
        AddNav(nav, "\uD83D\uDCAC   对话页", 1);
        AddNav(nav, "\u2699   设置", 2);
        AddNav(nav, "\uD83D\uDCC4   日志", 3);

        // 侧栏底部
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            BackColor = CSide, Padding = new Padding(16, 0, 16, 10),
        };
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        side.Controls.Add(bottom, 0, 2);
        _sideStatus = new Label { Dock = DockStyle.Fill, ForeColor = CSub, Font = new Font("Microsoft YaHei UI", 8.5F), Text = "服务：—" };
        bottom.Controls.Add(_sideStatus, 0, 0);
        var exitBtn = MakeFlatButton("退出程序", (s, e) => { _reallyExit = true; Close(); });
        exitBtn.Dock = DockStyle.Fill;
        bottom.Controls.Add(exitBtn, 0, 1);

        // 内容区
        var content = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Padding = new Padding(20, 16, 16, 16) };
        root.Controls.Add(content, 1, 0);

        _pages.Add(BuildConsolePage());
        _pages.Add(BuildWebPage());
        _pages.Add(BuildSettingsPage());
        _pages.Add(BuildLogPage());
        foreach (var p in _pages) { p.Dock = DockStyle.Fill; p.Visible = false; content.Controls.Add(p); }
        SwitchPage(0);
    }

    private void AddNav(FlowLayoutPanel nav, string text, int index)
    {
        var b = new Button
        {
            Text = text, Width = 190, Height = 42, FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0),
            BackColor = CSide, ForeColor = CText, Font = new Font("Microsoft YaHei UI", 10F),
            Cursor = Cursors.Hand, Margin = new Padding(0, 2, 0, 2),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = CAccentDim;
        b.Click += (s, e) => SwitchPage(index);
        nav.Controls.Add(b);
        _navBtns.Add(b);
    }

    private void SwitchPage(int index)
    {
        _page = index;
        for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == index;
        for (int i = 0; i < _navBtns.Count; i++)
        {
            _navBtns[i].BackColor = i == index ? CAccentDim : CSide;
            _navBtns[i].ForeColor = i == index ? CAccent : CText;
        }
        if (index == 1 && !_webTried) _ = InitWebAsync();
        if (index == 3) RefreshLog();
        RefreshStats();
    }

    private Button MakeFlatButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text, Height = 34, Width = 150, FlatStyle = FlatStyle.Flat, BackColor = CCard, ForeColor = CText,
            Font = new Font("Microsoft YaHei UI", 9F), Cursor = Cursors.Hand, Margin = new Padding(0, 4, 8, 4),
        };
        b.FlatAppearance.BorderColor = CLine;
        b.FlatAppearance.MouseOverBackColor = CAccentDim;
        if (onClick != null) b.Click += onClick;
        return b;
    }

    /* ---------- 控制台 ---------- */

    private Control BuildConsolePage()
    {
        _consoleFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = CBg,
        };
        _consoleFlow.Resize += (s, e) => FitCards();

        _consoleFlow.Controls.Add(new Label
        {
            Text = "控制台", AutoSize = true, ForeColor = CText,
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold), Margin = new Padding(2, 0, 0, 2),
        });
        _consoleFlow.Controls.Add(new Label
        {
            Text = "把 chat.deepseek.com 官网封装成本机 / 局域网的 OpenAI 兼容接口",
            AutoSize = true, ForeColor = CSub, Font = new Font("Microsoft YaHei UI", 9F), Margin = new Padding(2, 0, 0, 12),
        });

        var c1 = MakeCard("运行状态", 182);
        int y = 46;
        _vState = AddRow(c1, "服务状态", ref y);
        _vWeb = AddRow(c1, "网页状态", ref y);
        _vLogin = AddRow(c1, "登录状态", ref y);
        _vCalls = AddRow(c1, "调用统计", ref y);
        _vCtx = AddRow(c1, "会话上下文", ref y);
        _vLast = AddRow(c1, "最近一次", ref y);
        _consoleFlow.Controls.Add(c1);

        var c2 = MakeCard("接口信息", 134);
        y = 46;
        _vBase = AddRow(c2, "Base URL", ref y);
        _vLan = AddRow(c2, "局域网", ref y);
        _vKey = AddRow(c2, "API Key", ref y);
        _consoleFlow.Controls.Add(c2);

        var bar = new FlowLayoutPanel { Height = 46, AutoSize = false, Width = 760, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
        bar.Controls.Add(MakeFlatButton("复制 Base URL", (s, e) => Copy(Prefs.LanEnabled && ChatEngine.I.LanUrl.Length > 0 ? ChatEngine.I.LanUrl : "http://127.0.0.1:" + ChatEngine.I.Port + "/v1")));
        bar.Controls.Add(MakeFlatButton("复制 API Key", (s, e) => Copy(Prefs.ApiKey)));
        bar.Controls.Add(MakeFlatButton("复制本机地址", (s, e) => Copy("http://127.0.0.1:" + ChatEngine.I.Port + "/v1")));
        bar.Controls.Add(MakeFlatButton("查看健康状态", (s, e) => OpenUrl("http://127.0.0.1:" + ChatEngine.I.Port + "/health")));
        _consoleFlow.Controls.Add(bar);

        var c3 = MakeCard("常用操作", 100);
        var p3 = new FlowLayoutPanel { Left = 12, Top = 44, Width = 900, Height = 44, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        p3.Controls.Add(MakeFlatButton("启动服务", (s, e) => { ChatEngine.I.StartAll(Prefs.Port); Toast("服务已启动"); RefreshStats(); }));
        p3.Controls.Add(MakeFlatButton("停止服务", (s, e) => { ChatEngine.I.StopAll(); Toast("服务已停止"); RefreshStats(); }));
        p3.Controls.Add(MakeFlatButton("新建对话", (s, e) => NewChat()));
        p3.Controls.Add(MakeFlatButton("重载网页", (s, e) => ReloadWeb()));
        c3.Controls.Add(p3);
        _consoleFlow.Controls.Add(c3);

        var c4 = MakeCard("局域网访问", 128);
        c4.Controls.Add(new Label
        {
            Left = 16, Top = 44, Width = 860, Height = 42, ForeColor = CSub, Font = new Font("Microsoft YaHei UI", 8.5F),
            Text = "局域网设备把 Base URL 设为上面的「局域网」地址即可（需同一内网）。\n若无法访问：点右侧按钮放行防火墙（会弹 UAC），或手动执行复制的 netsh 命令。",
        });
        var p4 = new FlowLayoutPanel { Left = 12, Top = 88, Width = 900, Height = 40, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        p4.Controls.Add(MakeFlatButton("放行防火墙（需管理员）", (s, e) => AddFirewallRule()));
        p4.Controls.Add(MakeFlatButton("复制 netsh 命令", (s, e) => { Copy(FirewallCmd()); Toast("命令已复制"); }));
        c4.Controls.Add(p4);
        _consoleFlow.Controls.Add(c4);

        _consoleFlow.Controls.Add(new Panel { Height = 10, Width = 10 });
        return _consoleFlow;
    }

    private void FitCards()
    {
        if (_consoleFlow == null || _consoleFlow.IsDisposed) return;
        int w = Math.Max(460, _consoleFlow.ClientSize.Width - 12);
        foreach (var kv in _cardHeights)
        {
            if (kv.Key.IsDisposed) continue;
            kv.Key.Width = w;
            kv.Key.Height = kv.Value;
        }
    }

    private Panel MakeCard(string title, int height)
    {
        var card = new Panel { BackColor = CCard, Height = height, Width = 760, Margin = new Padding(0, 0, 0, 12) };
        card.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(CLine);
            using var path = Rounded(new Rectangle(0, 0, card.Width - 1, card.Height - 1), 10);
            g.DrawPath(pen, path);
        };
        card.Controls.Add(new Label
        {
            Text = title, Left = 16, Top = 14, AutoSize = true, ForeColor = CAccent,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        });
        _cardHeights[card] = height;
        return card;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private Label AddRow(Panel card, string name, ref int y)
    {
        card.Controls.Add(new Label { Text = name, Left = 16, Top = y + 2, AutoSize = true, ForeColor = CSub, Font = new Font("Microsoft YaHei UI", 9F) });
        var v = new Label { Text = "—", Left = 140, Top = y, AutoSize = true, ForeColor = CText, Font = new Font("Microsoft YaHei UI", 9.5F) };
        card.Controls.Add(v);
        y += 22;
        return v;
    }

    /* ---------- 对话页 ---------- */

    private Control BuildWebPage()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = CBg };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.Controls.Add(grid);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = CBg };
        bar.Controls.Add(MakeFlatButton("重载网页", (s, e) => ReloadWeb()));
        bar.Controls.Add(MakeFlatButton("重新注入脚本", (s, e) => { WebBridge.I.InjectBridge(); WebBridge.I.Probe(); Toast("已重新注入 bridge.js"); }));
        bar.Controls.Add(MakeFlatButton("新建对话", (s, e) => NewChat()));
        bar.Controls.Add(MakeFlatButton("外部浏览器打开", (s, e) => OpenUrl(DsUrl)));
        bar.Controls.Add(new Label
        {
            Text = "在此页登录官网；登录态与 API 共用同一会话。", AutoSize = true, ForeColor = CSub,
            Font = new Font("Microsoft YaHei UI", 8.5F), Margin = new Padding(10, 12, 0, 0),
        });
        grid.Controls.Add(bar, 0, 0);

        _webHost = new Panel { Dock = DockStyle.Fill, BackColor = CBg };
        grid.Controls.Add(_webHost, 0, 1);

        _webHint = new Label
        {
            Dock = DockStyle.Fill, ForeColor = CSub, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 10F),
            Text = "正在初始化 WebView2…",
        };
        _webHost.Controls.Add(_webHint);
        return host;
    }

    private async Task InitWebAsync()
    {
        if (_webTried) return;
        _webTried = true;
        try
        {
            string ver = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Log.Write("WebView2 运行时版本: " + ver);
        }
        catch (Exception e)
        {
            Log.Write("!! 未检测到 WebView2 运行时: " + e.Message);
            ShowWebUnavailable();
            return;
        }

        try
        {
            _webHint.Text = "正在加载 DeepSeek 官网…";
            _webHint.Visible = true;
            _web = new WebView2 { Dock = DockStyle.Fill };
            _webHost.Controls.Add(_web);
            _web.BringToFront();

            string udf = Path.Combine(Log.Dir, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, udf);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;

            try { core.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"; } catch { }
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsSwipeNavigationEnabled = false;

            core.WebMessageReceived += (s, ev) =>
            {
                try { WebBridge.I.OnWebMessage(ev.TryGetWebMessageAsString()); }
                catch (Exception ex) { Log.Write("消息处理失败: " + ex.Message); }
            };
            core.NavigationStarting += (s, ev) => WebBridge.I.MarkPageLoaded(false);
            core.NavigationCompleted += (s, ev) =>
            {
                WebBridge.I.MarkPageLoaded(ev.IsSuccess);
                Log.Write("页面加载 " + (ev.IsSuccess ? "成功" : "失败") + " url=" + core.Source);
                try { _webHint.Visible = false; } catch { }
                WebBridge.I.InjectBridge();
                WebBridge.I.Probe();
                RefreshStats();
            };
            core.ProcessFailed += (s, ev) =>
            {
                Log.Write("!! WebView2 进程异常: " + ev.ProcessFailedKind);
                WebBridge.I.Detach();
                ShowWebHint("WebView2 进程异常（" + ev.ProcessFailedKind + "），请点「重载网页」重试。");
            };
            core.NewWindowRequested += (s, ev) =>
            {
                ev.Handled = true;
                try
                {
                    if (ev.Uri.Contains("deepseek.com")) core.Navigate(ev.Uri);
                    else OpenUrl(ev.Uri);
                }
                catch { }
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebBridge.ShimJs);

            WebBridge.I.Attach(core);
            core.Navigate(DsUrl);
            Log.Write("WebView2 初始化完成，开始加载 " + DsUrl);
        }
        catch (Exception e)
        {
            Log.Write("!! WebView2 初始化失败: " + e.Message);
            try { if (_web != null) { _webHost.Controls.Remove(_web); _web.Dispose(); _web = null; } } catch { }
            ShowWebUnavailable();
        }
    }

    private void ShowWebHint(string text)
    {
        try
        {
            _webHint.Text = text;
            _webHint.Visible = true;
            _webHint.BringToFront();
        }
        catch { }
    }

    private void ShowWebUnavailable()
    {
        ShowWebHint("未检测到 Microsoft Edge WebView2 运行时。\n\n" +
                    "程序依赖 WebView2 驱动 DeepSeek 官网（HTTP 接口仍在运行，但调用会返回 503）。\n" +
                    "请安装「Evergreen 运行时」后重启本程序。\n\n" +
                    "Win11 / 新版 Win10 通常已内置。");
        try
        {
            var b = MakeFlatButton("打开 WebView2 下载页", (s, e) => OpenUrl(WebView2Download));
            b.Width = 190; b.Left = 20; b.Top = 20;
            b.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            b.Top = Math.Max(20, _webHost.ClientSize.Height - 60);
            _webHost.Resize += (s, e) => { try { b.Top = Math.Max(20, _webHost.ClientSize.Height - 60); } catch { } };
            _webHost.Controls.Add(b);
            b.BringToFront();
        }
        catch { }
    }

    /* ---------- 设置 ---------- */

    private Control BuildSettingsPage()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = CBg,
        };
        flow.Resize += (s, e) => FitCards();

        flow.Controls.Add(new Label { Text = "设置", AutoSize = true, ForeColor = CText, Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold), Margin = new Padding(2, 0, 0, 10) });

        var c1 = MakeCard("接口设置", 196);
        int y = 46;
        AddEditRow(c1, "端口", ref y, () => Prefs.Port.ToString(), v =>
        {
            if (int.TryParse(v, out var p) && p > 0 && p < 65536) { Prefs.Port = p; Prefs.Remove("port_active"); Toast("端口已保存，重启服务后生效"); }
            else Toast("端口无效");
        });
        AddEditRow(c1, "API Key", ref y, () => Prefs.ApiKey, v => { Prefs.Set("api_key", v); RefreshStats(); });
        AddEditRow(c1, "超时（秒）", ref y, () => Prefs.TimeoutSec.ToString(), v => { if (int.TryParse(v, out var t) && t > 5) Prefs.TimeoutSec = t; });
        AddCycleRow(c1, "思考策略", ref y, () => Prefs.ThinkingMode, v => { Prefs.ThinkingMode = v; ApplyModes(); }, new[] { "auto", "on", "off" });
        AddCycleRow(c1, "搜索策略", ref y, () => Prefs.SearchMode, v => { Prefs.SearchMode = v; ApplyModes(); }, new[] { "auto", "on", "off" });
        flow.Controls.Add(c1);

        var c2 = MakeCard("会话与上下文", 176);
        y = 46;
        AddSwitchRow(c2, "无状态模式", "每次发送完整对话（不依赖官网会话记忆）", ref y, () => Prefs.Stateless, v => Prefs.Stateless = v);
        AddSwitchRow(c2, "自动新开对话", "上下文达阈值时自动新建对话（旧会话保留）", ref y, () => Prefs.AutoNewChat, v => Prefs.AutoNewChat = v);
        AddEditRow(c2, "上下文上限（tokens）", ref y, () => Prefs.ContextTokens.ToString(), v => { if (int.TryParse(v, out var t) && t >= 8000) Prefs.ContextTokens = t; });
        AddEditRow(c2, "新对话阈值（%）", ref y, () => Prefs.NewChatThreshold.ToString(), v => { if (int.TryParse(v, out var t) && t >= 10 && t <= 100) Prefs.NewChatThreshold = t; });
        flow.Controls.Add(c2);

        var c3 = MakeCard("桌面行为", 176);
        y = 46;
        AddSwitchRow(c3, "关闭窗口即最小化到托盘", "服务继续在后台运行（推荐）", ref y, () => Prefs.CloseToTray, v => Prefs.CloseToTray = v);
        AddSwitchRow(c3, "开机自动启动", "登录 Windows 后自动运行并最小化到托盘", ref y, () => Prefs.AutoStart, v =>
        {
            Prefs.AutoStart = v;
            AutoRun.Set(v, Environment.ProcessPath);
        });
        AddSwitchRow(c3, "允许局域网访问", "监听 0.0.0.0（关闭后仅本机）", ref y, () => Prefs.LanEnabled, v => { Prefs.LanEnabled = v; Toast("重启服务后生效"); });
        flow.Controls.Add(c3);

        var c4 = MakeCard("高级", 108);
        var p4 = new FlowLayoutPanel { Left = 12, Top = 44, Width = 900, Height = 44, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        p4.Controls.Add(MakeFlatButton("打开数据目录", (s, e) => OpenFolder(Log.Dir)));
        p4.Controls.Add(MakeFlatButton("打开设置文件", (s, e) => OpenFile(Path.Combine(Log.Dir, "settings.json"))));
        p4.Controls.Add(MakeFlatButton("重置上下文计数", (s, e) => { ChatEngine.I.ResetContext(); Toast("上下文计数已重置"); }));
        c4.Controls.Add(p4);
        flow.Controls.Add(c4);

        flow.Controls.Add(new Panel { Height = 10, Width = 10 });
        return flow;
    }

    private void AddEditRow(Panel card, string name, ref int y, Func<string> get, Action<string> set)
    {
        card.Controls.Add(new Label { Text = name, Left = 16, Top = y + 2, AutoSize = true, ForeColor = CSub, Font = new Font("Microsoft YaHei UI", 9F) });
        var v = new Label { Text = get(), Left = 300, Top = y, AutoSize = true, ForeColor = CAccent, Font = new Font("Microsoft YaHei UI", 9.5F) };
        var edit = MakeFlatButton("修改", (s, e) =>
        {
            string r = InputBox.Show(this, "修改 " + name, get());
            if (r == null) return;
            set(r.Trim());
            v.Text = get();
            RefreshStats();
        });
        edit.Left = 470; edit.Top = y - 4; edit.Width = 62; edit.Height = 24;
        card.Controls.Add(v);
        card.Controls.Add(edit);
        y += 26;
    }

    private void AddCycleRow(Panel card, string name, ref int y, Func<string> get, Action<string> set, string[] values)
    {
        card.Controls.Add(new Label { Text = name, Left = 16, Top = y + 2, AutoSize = true, ForeColor = CSub, Font = new Font("Microsoft YaHei UI", 9F) });
        var v = new Label { Text = get(), Left = 300, Top = y, AutoSize = true, ForeColor = CAccent, Font = new Font("Microsoft YaHei UI", 9.5F) };
        var b = MakeFlatButton("切换", (s, e) =>
        {
            string cur = get();
            int idx = Array.IndexOf(values, cur);
            string next = values[(idx + 1 + values.Length) % values.Length];
            set(next);
            v.Text = get();
        });
        b.Left = 470; b.Top = y - 4; b.Width = 62; b.Height = 24;
        card.Controls.Add(v);
        card.Controls.Add(b);
        y += 26;
    }

    private void AddSwitchRow(Panel card, string name, string desc, ref int y, Func<bool> get, Action<bool> set)
    {
        card.Controls.Add(new Label { Text = name, Left = 16, Top = y + 1, AutoSize = true, ForeColor = CText, Font = new Font("Microsoft YaHei UI", 9F) });
        card.Controls.Add(new Label { Text = desc, Left = 300, Top = y + 3, AutoSize = true, ForeColor = CSub, Font = new Font("Microsoft YaHei UI", 8F) });
        bool on = get();
        var cb = new CheckBox
        {
            Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
            Left = 470, Top = y - 2, Width = 64, Height = 24, Checked = on,
            BackColor = on ? CAccentDim : CCard, ForeColor = on ? CAccent : CSub,
            Text = on ? "已开启" : "已关闭",
        };
        cb.FlatAppearance.BorderColor = CLine;
        cb.CheckedChanged += (s, e) =>
        {
            try
            {
                set(cb.Checked);
                cb.Text = cb.Checked ? "已开启" : "已关闭";
                cb.BackColor = cb.Checked ? CAccentDim : CCard;
                cb.ForeColor = cb.Checked ? CAccent : CSub;
            }
            catch (Exception ex) { Toast("设置失败：" + ex.Message); }
        };
        card.Controls.Add(cb);
        y += 30;
    }

    private void ApplyModes() => WebBridge.I.Configure(Prefs.ThinkingMode, Prefs.SearchMode);

    /* ---------- 日志 ---------- */

    private Control BuildLogPage()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = CBg };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.Controls.Add(grid);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = CBg };
        bar.Controls.Add(MakeFlatButton("刷新", (s, e) => RefreshLog()));
        bar.Controls.Add(MakeFlatButton("清空界面", (s, e) => { Log.Clear(); RefreshLog(); }));
        bar.Controls.Add(MakeFlatButton("打开日志文件", (s, e) => OpenFile(Log.FilePath)));
        bar.Controls.Add(MakeFlatButton("打开数据目录", (s, e) => OpenFolder(Log.Dir)));
        bar.Controls.Add(MakeFlatButton("复制全部", (s, e) => { Copy(_logBox.Text); Toast("日志已复制"); }));
        grid.Controls.Add(bar, 0, 0);

        _logBox = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, BackColor = Color.FromArgb(0x0B, 0x0E, 0x12),
            ForeColor = Color.FromArgb(0xC8, 0xD0, 0xDC), Font = new Font("Consolas", 9F), BorderStyle = BorderStyle.None,
        };
        grid.Controls.Add(_logBox, 0, 1);
        return host;
    }

    private void RefreshLog()
    {
        try
        {
            if (_logBox == null || _logBox.IsDisposed) return;
            string t = Log.Text();
            if (_logBox.Text == t) return;
            _logBox.Text = t;
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
        catch { }
    }

    /* ---------- 托盘 ---------- */

    private void BuildTray()
    {
        _tray = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Visible = true,
            Text = Program.AppName + " v" + Program.Version,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (s, e) => ShowFromTray());
        menu.Items.Add("打开对话页", null, (s, e) => { ShowFromTray(); SwitchPage(1); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("复制 Base URL", null, (s, e) => Copy(Prefs.LanEnabled && ChatEngine.I.LanUrl.Length > 0 ? ChatEngine.I.LanUrl : "http://127.0.0.1:" + ChatEngine.I.Port + "/v1"));
        menu.Items.Add("复制 API Key", null, (s, e) => Copy(Prefs.ApiKey));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("新建对话", null, (s, e) => NewChat());
        menu.Items.Add("重载网页", null, (s, e) => ReloadWeb());
        menu.Items.Add("启动服务", null, (s, e) => { ChatEngine.I.StartAll(Prefs.Port); RefreshStats(); });
        menu.Items.Add("停止服务", null, (s, e) => { ChatEngine.I.StopAll(); RefreshStats(); });
        menu.Items.Add(new ToolStripSeparator());
        _trayState = new ToolStripMenuItem("状态：—") { Enabled = false };
        menu.Items.Add(_trayState);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (s, e) => { _reallyExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => ShowFromTray();
        _tray.BalloonTipTitle = Program.AppName;
        _tray.BalloonTipText = "已缩小到系统托盘，服务继续运行；双击图标可再次打开。";
    }

    private void MinimizeToTray(bool balloon)
    {
        try
        {
            Hide();
            if (balloon && !_balloonShown)
            {
                _balloonShown = true;
                _tray?.ShowBalloonTip(3000);
            }
        }
        catch { }
    }

    private void ShowFromTray()
    {
        try
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }
        catch { }
    }

    private void StartSingleInstanceWatcher()
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\DeepSeekWebAPI_Show");
            _showWatcher = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (!_showEvent.WaitOne()) break;
                        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(ShowFromTray));
                    }
                    catch (Exception) { break; }
                }
            })
            { IsBackground = true, Name = "single-instance" };
            _showWatcher.Start();
        }
        catch { }
    }

    /* ---------- 定时刷新 ---------- */

    private void OnTick(object sender, EventArgs e)
    {
        _tick++;
        if (_tick % 5 == 0) WebBridge.I.Probe();
        if (_tick % 60 == 0 && WebBridge.I.IsAttached) WebBridge.I.InjectBridge();
        RefreshStats();
        if (_page == 3 && _tick % 3 == 0) RefreshLog();
    }

    private void RefreshStats()
    {
        try
        {
            var eng = ChatEngine.I;
            if (_vState != null) _vState.Text = eng.State;
            if (_vWeb != null) _vWeb.Text = WebBridge.I.IsAttached ? (WebBridge.I.IsReady ? "已加载（bridge 就绪）" : "加载中…") : "未初始化";
            if (_vLogin != null) _vLogin.Text = eng.IsLoggedInText;
            if (_vCalls != null) _vCalls.Text = eng.TotalCallsText + (eng.Inflight > 0 ? "，进行中 " + eng.Inflight : "");
            if (_vCtx != null) _vCtx.Text = eng.ContextInfoText;
            if (_vLast != null) _vLast.Text = eng.LastCallInfo;
            if (_vBase != null) _vBase.Text = "http://127.0.0.1:" + eng.Port + "/v1";
            if (_vLan != null) _vLan.Text = eng.LanUrl;
            if (_vKey != null) _vKey.Text = Prefs.ApiKey;
            if (_sideStatus != null) _sideStatus.Text = "服务：" + eng.State + "\n本机：" + eng.Port + " · 局域网：" + (Prefs.LanEnabled ? "开" : "关");
            if (_tray != null && _tray.Visible)
            {
                _tray.Text = Program.AppName + " · " + (eng.LanUrl.Length > 0 ? eng.LanUrl : ("127.0.0.1:" + eng.Port));
                if (_trayState != null) _trayState.Text = "状态：" + eng.State + " · " + eng.TotalCallsText;
            }
        }
        catch { }
    }

    /* ---------- 行为 ---------- */

    private void NewChat()
    {
        var r = WebBridge.I.NewChat();
        ChatEngine.I.ResetContext();
        Toast(r.Bool("ok") ? "已新建对话" : "新建对话失败：" + r.Str("error"));
    }

    private void ReloadWeb()
    {
        try
        {
            if (_web?.CoreWebView2 != null) { _web.CoreWebView2.Navigate(DsUrl); Toast("正在重载官网…"); }
            else { _webTried = false; _ = InitWebAsync(); }
        }
        catch (Exception e) { Toast("重载失败：" + e.Message); }
    }

    private string FirewallCmd()
    {
        int port = ChatEngine.I.Port > 0 ? ChatEngine.I.Port : Prefs.Port;
        return "netsh advfirewall firewall add rule name=\"DeepSeek Web API\" dir=in action=allow protocol=TCP localport=" + port;
    }

    private void AddFirewallRule()
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + FirewallCmd())
            {
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
            Toast("已请求放行防火墙（请在 UAC 弹窗中允许）");
        }
        catch (Exception e) { Toast("放行失败（可能取消了 UAC）：" + e.Message); }
    }

    private static void Copy(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return;
            Clipboard.SetText(text);
        }
        catch { }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); } catch { }
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else OpenFolder(Path.GetDirectoryName(path));
        }
        catch { }
    }

    private void Toast(string msg)
    {
        Log.Write("[界面] " + msg);
        try { _tray?.ShowBalloonTip(2000, Program.AppName, msg, ToolTipIcon.Info); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && Prefs.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            MinimizeToTray(true);
            return;
        }
        try { _timer?.Stop(); } catch { }
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
        try { _web?.Dispose(); } catch { }
        try { ChatEngine.I.StopAll(); } catch { }
        base.OnFormClosing(e);
    }

    /// <summary>把 bridge 状态转发给引擎（UI 线程）。</summary>
    private sealed class StatusForwarder : WebBridge.IStatusListener
    {
        private readonly MainForm _f;
        public StatusForwarder(MainForm f) { _f = f; }
        public void OnStatus(string json)
        {
            ChatEngine.I.OnStatus(json);
            try { if (_f.IsHandleCreated && !_f.IsDisposed) _f.BeginInvoke(new Action(_f.RefreshStats)); } catch { }
        }
    }
}
