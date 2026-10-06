using System.Diagnostics;
using System.Drawing.Drawing2D;
using DSWebApi.Desktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DSWebApi.Desktop.UI;

/// <summary>
/// 桌面主窗口：左侧导航 + 两个页面（控制台 / 对话页），与 APK 的信息架构保持一致。
/// 全部布局用 TableLayoutPanel/Percent 做响应式，窗口放大时内容等比铺满；
/// 另有全局「界面缩放」（托盘菜单）用于等比放大字号与控件。
/// </summary>
public sealed class MainForm : Form
{
    /* ---------------- 配色 ---------------- */
    private static readonly Color CBg = Color.FromArgb(0x0B, 0x0E, 0x14);
    private static readonly Color CSide = Color.FromArgb(0x0F, 0x13, 0x1A);
    private static readonly Color CCard = Color.FromArgb(0x15, 0x1A, 0x23);
    private static readonly Color CLine = Color.FromArgb(0x24, 0x2B, 0x38);
    private static readonly Color CText = Color.FromArgb(0xE8, 0xEC, 0xF4);
    private static readonly Color CSub = Color.FromArgb(0x8B, 0x95, 0xA8);
    private static readonly Color CAccent = Color.FromArgb(0x4C, 0x8D, 0xFF);
    private static readonly Color CAccentDim = Color.FromArgb(0x18, 0x24, 0x3D);
    private static readonly Color COk = Color.FromArgb(0x36, 0xD3, 0x99);
    private static readonly Color CWarn = Color.FromArgb(0xF5, 0xA5, 0x24);
    private static readonly Color CBad = Color.FromArgb(0xF8, 0x71, 0x71);

    /* 统一的间距 / 字号层级（避免「这又是小字、那又是拥挤」） */
    private int Gap => (int)(14 * _s);          // 卡片之间
    private int Pad => (int)(20 * _s);          // 卡片内边距
    private int RowH => (int)(32 * _s);         // 键值行高
    private float FS_Title => 13.5f;            // 卡片标题
    private float FS_Body => 11.5f;             // 正文
    private float FS_Value => 12.5f;            // 键值
    private float FS_Page => 22f;               // 页面大标题

    private const string DsUrl = "https://chat.deepseek.com/";
    private const string WebHintText = "在此页登录 DeepSeek 官网；登录态由本程序保存，接口调用共用同一会话。";

    /* ---------------- 状态 ---------------- */
    private readonly bool _startMinimized;
    private float _s = 1f;                       // 界面缩放系数
    private readonly List<Button> _navBtns = new List<Button>();
    private readonly List<Control> _pages = new List<Control>();
    private int _page;
    private bool _reallyExit;

    private Panel _content;
    private Label _toast;
    private TableLayoutPanel _acctTable;                       // 控制台「账号调用情况」表
    private readonly Dictionary<string, Label[]> _tblRows = new Dictionary<string, Label[]>();
    private Label _acctHead;                                   // 侧栏「账号」区的小状态
    private FlowLayoutPanel _acctFlow;          // 侧栏账号列表
    private FlowLayoutPanel _acctChips;         // 对话页账号切换条
    private Panel _acctHost;                    // 账号区容器
    private Label _vRotate;                     // 控制台「多账号轮换」状态
    private string _currentAccountId = "primary";
    private List<string> _acctOrder = new List<string>();
    private readonly Dictionary<string, Button> _acctBtns = new Dictionary<string, Button>();
    private readonly Dictionary<string, Button> _chipBtns = new Dictionary<string, Button>();
    private System.Windows.Forms.Timer _toastTimer;

    private Panel _webHost;
    private Label _webHint;
    private WebView2 _web;
    private bool _webTried;
    private Label _zoomText;
    private bool _balloonShown;

    private Label _vState, _vWeb, _vLogin;
    private Label _vBase, _vLan, _vKey;
    private Label _vCalls, _vCtx, _vLast;
    private Label _sideState;

    private NotifyIcon _tray;
    private ToolStripMenuItem _trayState;
    private System.Windows.Forms.Timer _timer;
    private int _tick;
    private EventWaitHandle _showEvent;
    private Thread _showWatcher;

    public MainForm(bool startMinimized)
    {
        _startMinimized = startMinimized;
        _s = Math.Max(0.8f, Math.Min(2.0f, Prefs.UiScalePct / 100f));
        try { AccountPool.I.LoadFromPrefs(); } catch (Exception e) { Log.Write("账号池载入失败: " + e.Message); }

        Text = Program.AppName + " v" + Program.Version + " · Windows";
        BackColor = CBg;
        ForeColor = CText;
        MinimumSize = new Size(1080, 700);
        Size = new Size(1440, 900);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10.5F * _s);
        try { var ico = Icon.ExtractAssociatedIcon(Environment.ProcessPath); if (ico != null) Icon = ico; } catch { }

        BuildUi();
        BuildTray();
        StartSingleInstanceWatcher();

        ChatEngine.I.Changed += () =>
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(RefreshStats)); } catch { }
        };
        WebBridge.I.Name = "主账号";
        WebBridge.I.SetStatusListener(new StatusForwarder(this, AccountPool.I.Find("primary")));
        AccountPool.I.Changed += () =>
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(RefreshAccounts)); } catch { }
        };

        _toastTimer = new System.Windows.Forms.Timer { Interval = 3200 };
        _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); try { if (_toast != null) _toast.Visible = false; } catch { } };

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += OnTick;
        _timer.Start();

        Shown += (s, e) =>
        {
            Log.Write("界面已显示");
            ApplyDarkChrome();
            try { BeginInvoke(new Action(() => { ApplyDarkChrome(); RefreshAccounts(); })); } catch { }
            ChatEngine.I.StartAll(Prefs.Port);
            RefreshStats();
            _ = InitWebAsync();
            if (_startMinimized) MinimizeToTray(true);
        };
    }

    /* ================= 字体 / 控件工厂 ================= */

    private Font F(float size, FontStyle st = FontStyle.Regular) =>
        new Font("Microsoft YaHei UI", Math.Max(6f, size * _s), st);

    private Button FlatBtn(string text, EventHandler onClick, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size((int)(74 * _s), (int)(40 * _s)),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? CAccentDim : CCard,
            ForeColor = primary ? CAccent : CText,
            Font = F(FS_Body),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, (int)(10 * _s), 0),
            Padding = new Padding((int)(12 * _s), 0, (int)(12 * _s), 0),
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = primary ? CAccent : CLine;
        b.FlatAppearance.MouseOverBackColor = CAccentDim;
        if (onClick != null) b.Click += onClick;
        return b;
    }

    /// <summary>等分按钮行：按钮按列等分占满卡片宽度，卡片再窄也不会把按钮挤出去。</summary>
    private TableLayoutPanel BtnRow(params (string text, EventHandler onClick)[] items)
    {
        int n = Math.Max(1, items.Length);
        var t = new TableLayoutPanel
        {
            ColumnCount = n, RowCount = 1, Height = (int)(42 * _s),
            BackColor = Color.Transparent, Margin = Padding.Empty, Tag = "fillw",
        };
        for (int i = 0; i < n; i++)
        {
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / n));
            var b = FlatBtn(items[i].text, items[i].onClick, i == 0);
            b.AutoSize = false;
            b.Dock = DockStyle.Fill;
            b.MinimumSize = Size.Empty;
            b.Padding = new Padding((int)(4 * _s), 0, (int)(4 * _s), 0);
            b.Margin = new Padding(i == 0 ? 0 : (int)(5 * _s), 0, i == n - 1 ? 0 : (int)(5 * _s), 0);
            t.Controls.Add(b, i, 0);
        }
        return t;
    }

    /// <summary>圆角卡片容器（统一外观）。</summary>
    private Panel Card(string title, int height)
    {
        var card = new Panel
        {
            BackColor = CCard,
            Height = (int)(height * _s),
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, Gap),
        };
        bool hasTitle = title != null;
        card.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(CLine);
            using var path = Rounded(new Rectangle(0, 0, card.Width - 1, card.Height - 1), (int)(12 * _s));
            g.DrawPath(pen, path);
            if (hasTitle)
            {
                using var br = new SolidBrush(CAccent);
                g.FillRectangle(br, (int)(Pad * 0.6), (int)(20 * _s), (int)(3 * _s), (int)(15 * _s));
            }
        };
        if (hasTitle)
        {
            card.Controls.Add(new Label
            {
                Text = title, Left = (int)(Pad * 0.6 + 11 * _s), Top = (int)(17 * _s), AutoSize = true,
                ForeColor = CText, Font = F(FS_Title, FontStyle.Bold), BackColor = Color.Transparent,
            });
        }
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

    /// <summary>粗略估算文本像素宽（中日韩按 1.35 em，其余 0.6 em；磅→像素 ≈ ×1.34）。</summary>
    private static float TextPx(string s, float pt)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        float w = 0;
        foreach (char ch in s) w += (ch > 0x2E80 ? 1.35f : 0.6f) * pt;
        return w * 1.34f;
    }

    /// <summary>磁贴数值：长文本自动缩小字号，避免被磁贴裁掉（只在文字变化时重算，避免每秒新建字体）。</summary>
    private void FitTile(Label v, string text, Color c)
    {
        try
        {
            if (v.Text != text)
            {
                v.Text = text;
                int avail = (v.Parent != null ? v.Parent.ClientSize.Width : (int)(320 * _s)) - (int)(52 * _s);
                if (avail < (int)(80 * _s)) avail = (int)(80 * _s);
                float size = 18f;
                while (size > 10f && TextPx(text, size * _s) > avail) size -= 0.5f;
                v.Font = F(size, FontStyle.Bold);
            }
            v.ForeColor = c;
        }
        catch { }
    }

    private Label SmallLabel(string text, Color c) => new Label
    {
        Text = text, AutoSize = true, ForeColor = c, Font = F(11.5f), BackColor = Color.Transparent,
    };

    /* ================= 布局 ================= */

    private void BuildUi()
    {
        _navBtns.Clear();
        _pages.Clear();
        SuspendLayout();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = CBg,
            Margin = Padding.Empty, Padding = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300 * _s));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        /* ---------- 侧边栏 ---------- */
        var side = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = CSide, Margin = Padding.Empty,
        };
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 96 * _s));    // 品牌
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 122 * _s));   // 导航
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));         // 账号
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 152 * _s));   // 底部
        root.Controls.Add(side, 0, 0);

        /* 品牌 */
        var head = new Panel { Dock = DockStyle.Fill, BackColor = CSide };
        try
        {
            if (Icon != null)
                head.Controls.Add(new PictureBox
                {
                    Left = (int)(20 * _s), Top = (int)(26 * _s), Width = (int)(40 * _s), Height = (int)(40 * _s),
                    SizeMode = PictureBoxSizeMode.Zoom, Image = Icon.ToBitmap(), BackColor = Color.Transparent,
                });
        }
        catch { }
        head.Controls.Add(new Label
        {
            Text = "DeepSeek Web API", Left = (int)(72 * _s), Top = (int)(24 * _s), AutoSize = true,
            ForeColor = CText, Font = F(14f, FontStyle.Bold), BackColor = Color.Transparent,
        });
        head.Controls.Add(new Label
        {
            Text = "Windows 桌面版 · v" + Program.Version, Left = (int)(72 * _s), Top = (int)(50 * _s), AutoSize = true,
            ForeColor = CSub, Font = F(10.5f), BackColor = Color.Transparent,
        });
        side.Controls.Add(head, 0, 0);

        /* 导航 */
        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = CSide, Padding = new Padding((int)(16 * _s), (int)(8 * _s), (int)(16 * _s), (int)(8 * _s)),
        };
        side.Controls.Add(nav, 0, 1);
        AddNav(nav, "\u25A0   控制台", 0);
        AddNav(nav, "\u25A1   对话页", 1);

        /* 账号区 */
        _acctHost = new Panel { Dock = DockStyle.Fill, BackColor = CSide, Padding = new Padding((int)(16 * _s), (int)(2 * _s), (int)(16 * _s), (int)(6 * _s)) };
        var acctGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = CSide, Margin = Padding.Empty };
        acctGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34 * _s));
        acctGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        acctGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52 * _s));
        _acctHost.Controls.Add(acctGrid);

        var acctHeadRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = CSide, Margin = Padding.Empty };
        acctHeadRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        acctHeadRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130 * _s));
        acctHeadRow.Controls.Add(new Label
        {
            Text = "账号", Dock = DockStyle.Fill, ForeColor = CSub, Font = F(10.5f),
            TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent,
        }, 0, 0);
        _acctHead = new Label
        {
            Text = "—", Dock = DockStyle.Fill, ForeColor = CSub, Font = F(10.5f),
            TextAlign = ContentAlignment.MiddleRight, BackColor = Color.Transparent,
        };
        acctHeadRow.Controls.Add(_acctHead, 1, 0);
        acctGrid.Controls.Add(acctHeadRow, 0, 0);

        _acctFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = CSide, Margin = Padding.Empty,
        };
        _acctFlow.Resize += (s2, e2) =>
        {
            try
            {
                int w = Math.Max((int)(160 * _s), _acctFlow.ClientSize.Width - (int)(6 * _s));
                foreach (Control c in _acctFlow.Controls) c.Width = w;
            }
            catch { }
        };
        acctGrid.Controls.Add(_acctFlow, 0, 1);
        var addAcct = FlatBtn("＋ 添加账号", (s2, e2) => AddAccountInteractive());
        addAcct.Dock = DockStyle.Fill;
        addAcct.MinimumSize = Size.Empty;
        addAcct.Margin = new Padding(0, (int)(6 * _s), 0, 0);
        acctGrid.Controls.Add(addAcct, 0, 2);
        side.Controls.Add(_acctHost, 0, 2);

        /* 底部 */
        var foot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = CSide,
            Padding = new Padding((int)(16 * _s), 0, (int)(16 * _s), (int)(14 * _s)),
        };
        foot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 46 * _s));
        foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 48 * _s));
        foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 48 * _s));
        _sideState = new Label
        {
            Dock = DockStyle.Fill, ForeColor = CSub, Font = F(11f), BackColor = Color.Transparent,
            Text = "服务：—",
        };
        foot.Controls.Add(_sideState, 0, 1);
        var stBtn = FlatBtn("设置…", (s, e) => ShowSettings());
        stBtn.Dock = DockStyle.Fill; stBtn.MinimumSize = Size.Empty; stBtn.Margin = new Padding(0, 0, 0, (int)(8 * _s));
        foot.Controls.Add(stBtn, 0, 2);
        var exitBtn = FlatBtn("退出程序", (s, e) => { _reallyExit = true; Close(); });
        exitBtn.Dock = DockStyle.Fill; exitBtn.MinimumSize = Size.Empty; exitBtn.Margin = Padding.Empty;
        foot.Controls.Add(exitBtn, 0, 3);
        side.Controls.Add(foot, 0, 3);

        /* ---------- 内容区 ---------- */
        _content = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Padding = new Padding((int)(26 * _s), (int)(22 * _s), (int)(26 * _s), (int)(14 * _s)) };
        root.Controls.Add(_content, 1, 0);

        _pages.Add(BuildConsolePage());
        _pages.Add(BuildWebPage());
        foreach (var p in _pages) { p.Dock = DockStyle.Fill; p.Visible = false; _content.Controls.Add(p); }

        _toast = new Label
        {
            Dock = DockStyle.Bottom, Height = (int)(34 * _s), TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CAccent, Font = F(10.5f), Visible = false, BackColor = Color.Transparent,
        };
        _content.Controls.Add(_toast);
        _toast.BringToFront();

        SwitchPage(0, force: true);
        ResumeLayout(true);
    }

    private void AddNav(FlowLayoutPanel nav, string text, int index)
    {
        var b = new Button
        {
            Text = text,
            Width = (int)(268 * _s),
            Height = (int)(52 * _s),
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding((int)(18 * _s), 0, 0, 0),
            BackColor = CSide,
            ForeColor = CSub,
            Font = F(13f),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, (int)(3 * _s), 0, (int)(3 * _s)),
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = CAccentDim;
        b.Click += (s, e) => SwitchPage(index);
        nav.Controls.Add(b);
        _navBtns.Add(b);
    }

    private void SwitchPage(int index, bool force = false)
    {
        if (!force && _page == index) return;
        _page = index;
        for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == index;
        for (int i = 0; i < _navBtns.Count; i++)
        {
            _navBtns[i].BackColor = i == index ? CAccentDim : CSide;
            _navBtns[i].ForeColor = i == index ? CAccent : CSub;
            _navBtns[i].Font = F(13f, i == index ? FontStyle.Bold : FontStyle.Regular);
        }
        if (index == 1 && !_webTried) _ = InitWebAsync();
        RefreshStats();
    }

    /* ---------- 控制台页 ---------- */

    private Control BuildConsolePage()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = CBg, AutoScroll = true };
        var grid = new TableLayoutPanel
        {
            AutoSize = false, Location = new Point(0, 0), ColumnCount = 2,
            BackColor = CBg, Margin = Padding.Empty, Padding = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        int[] rows = { 46, 30, 122, 258, 266, 232, 244, 12 };
        foreach (var r in rows) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, (int)(r * _s)));
        host.Controls.Add(grid);

        void Sync()
        {
            try
            {
                int w = Math.Max((int)(520 * _s), host.ClientSize.Width - 8);
                grid.Width = w;
                int h = 0;
                for (int i = 0; i < grid.RowStyles.Count; i++) h += (int)grid.RowStyles[i].Height;
                grid.Height = h;
            }
            catch { }
        }
        host.HandleCreated += (s, e) => Sync();
        host.ClientSizeChanged += (s, e) => Sync();

        /* 标题 */
        var t = new Label
        {
            Text = "控制台", AutoSize = true, ForeColor = CText, Font = F(FS_Page, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, (int)(4 * _s)), BackColor = Color.Transparent,
        };
        grid.Controls.Add(t, 0, 0);
        grid.SetColumnSpan(t, 2);
        var sub = new Label
        {
            Text = "把 chat.deepseek.com 官网封装成本机 / 局域网的 OpenAI 兼容接口",
            AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CSub, Font = F(FS_Body), Margin = new Padding(0, 0, 0, (int)(14 * _s)), BackColor = Color.Transparent,
        };
        grid.Controls.Add(sub, 0, 1);
        grid.SetColumnSpan(sub, 2);

        /* 状态磁贴 */
        var tiles = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 1, Dock = DockStyle.Fill, BackColor = CBg,
            Margin = new Padding(0, 0, 0, (int)(14 * _s)),
        };
        for (int i = 0; i < 3; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        tiles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _vState = StatTile(tiles, 0, "服务状态", CWarn);
        _vWeb = StatTile(tiles, 1, "网页 / 桥接", CWarn);
        _vLogin = StatTile(tiles, 2, "登录状态", CWarn);
        grid.Controls.Add(tiles, 0, 2);
        grid.SetColumnSpan(tiles, 2);

        /* 接口信息 */
        var c1 = Card("接口信息", 246);
        int y = (int)(56 * _s);
        _vBase = KV(c1, "本机地址", ref y);
        _vLan = KV(c1, "局域网地址", ref y);
        _vKey = KV(c1, "API Key", ref y);
        var b1 = BtnRow(
            ("复制本机", (s2, e2) => Copy(BaseUrl(true))),
            ("复制 Key", (s2, e2) => Copy(Prefs.ApiKey)),
            ("复制局域网", (s2, e2) => Copy(BaseUrl(false))));
        b1.Left = Pad; b1.Top = y + (int)(12 * _s); b1.Width = (int)(520 * _s);
        c1.Controls.Add(b1);
        c1.Resize += (s2, e2) => FitTracked(c1);
        grid.Controls.Add(c1, 0, 3);

        /* 多账号轮换 */
        var c5 = Card("多账号轮换", 246);
        int y5 = (int)(56 * _s);
        _vRotate = KV(c5, "轮换状态", ref y5);
        var c5note = new Label
        {
            Left = Pad, Top = y5 + (int)(2 * _s), Width = (int)(520 * _s), Height = (int)(74 * _s),
            AutoSize = false, ForeColor = CSub, Font = F(10.5f), BackColor = Color.Transparent, Tag = "fillw",
            Text = "顺序轮流：每个账号在同一对话里连发 " + Prefs.SessionSendLimit + " 次 → 换下一个账号；"
                 + "全员轮完一圈 → 各账号新开对话；命中「消息发送频繁」自动冷却 " + Prefs.CooldownMinutes + " 分钟。",
        };
        c5.Controls.Add(c5note);
        var b5 = BtnRow(
            ("轮换开关", (s2, e2) => ToggleRotate()),
            ("＋ 账号", (s2, e2) => AddAccountInteractive()),
            ("连发次数…", (s2, e2) => EditSendLimit()));
        b5.Left = Pad; b5.Top = y5 + (int)(84 * _s); b5.Width = (int)(520 * _s);
        c5.Controls.Add(b5);
        c5.Resize += (s2, e2) => FitTracked(c5);
        grid.Controls.Add(c5, 1, 3);

        /* 账号调用情况 */
        var c6 = Card("账号调用情况（轮换顺序）", 254);
        _acctTable = new TableLayoutPanel
        {
            Left = Pad, Top = (int)(56 * _s), Width = (int)(520 * _s), Height = (int)(182 * _s),
            ColumnCount = 6, BackColor = Color.Transparent, AutoScroll = true, Margin = Padding.Empty,
        };
        _acctTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38 * _s));
        _acctTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        _acctTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        _acctTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));
        _acctTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        _acctTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        c6.Resize += (s2, e2) => { try { _acctTable.Width = Math.Max((int)(420 * _s), c6.ClientSize.Width - Pad * 2); } catch { } };
        c6.Controls.Add(_acctTable);
        grid.Controls.Add(c6, 0, 4);
        grid.SetColumnSpan(c6, 2);

        /* 运行数据 */
        var c2 = Card("运行数据", 220);
        y = (int)(56 * _s);
        _vCalls = KV(c2, "调用统计", ref y);
        _vCtx = KV(c2, "会话上下文", ref y);
        _vLast = KV(c2, "最近一次", ref y);
        grid.Controls.Add(c2, 0, 5);

        /* 常用操作 */
        var c3 = Card("常用操作", 220);
        var b3 = BtnRow(
            ("新建对话", (s2, e2) => NewChat()),
            ("重载网页", (s2, e2) => ReloadWeb()),
            ("检查更新", (s2, e2) => CheckUpdate()));
        b3.Left = Pad; b3.Top = (int)(54 * _s); b3.Width = (int)(520 * _s);
        var b3b = BtnRow(
            ("打开设置", (s2, e2) => ShowSettings()),
            ("打开日志", (s2, e2) => OpenFile(Log.FilePath)),
            ("数据目录", (s2, e2) => OpenFolder(Log.Dir)));
        b3b.Left = Pad; b3b.Top = (int)(102 * _s); b3b.Width = (int)(520 * _s);
        var b3c = BtnRow(("放行防火墙（需管理员权限）", (s2, e2) => AddFirewallRule()));
        b3c.Left = Pad; b3c.Top = (int)(150 * _s); b3c.Width = (int)(520 * _s);
        c3.Controls.Add(b3);
        c3.Controls.Add(b3b);
        c3.Controls.Add(b3c);
        c3.Resize += (s2, e2) => FitTracked(c3);
        grid.Controls.Add(c3, 1, 5);

        /* 客户端填写 */
        var c4 = Card("客户端填写（OpenAI 兼容）", 232);
        c4.Controls.Add(new Label
        {
            Left = Pad, Top = (int)(58 * _s), Width = (int)(520 * _s), Height = (int)(104 * _s),
            AutoSize = false, ForeColor = CText, Font = F(12f), BackColor = Color.Transparent, Tag = "fillw",
            Text = "Base URL : http://127.0.0.1:8787/v1\r\n" +
                   "API Key  : " + Prefs.ApiKey + "\r\n" +
                   "Model    : deepseek",
        });
        c4.Controls.Add(new Label
        {
            Left = Pad, Top = (int)(166 * _s), Width = (int)(520 * _s), Height = (int)(58 * _s),
            AutoSize = false, ForeColor = CSub, Font = F(10.5f), BackColor = Color.Transparent, Tag = "fillw",
            Text = "局域网访问（设备端 Base URL 换成上面的局域网地址）需放行防火墙；\r\n" +
                   "调用前请先在「对话页」登录官网，未登录时接口返回 503。\r\n" +
                   "升级：下载新版 exe，关闭程序后直接覆盖旧文件（登录态与设置不受影响）。",
        });
        grid.Controls.Add(c4, 0, 6);
        grid.SetColumnSpan(c4, 2);

        return host;
    }

    private Control Wrap(Panel card)
    {
        var holder = new Panel { Dock = DockStyle.Top, Height = card.Height, BackColor = CBg, Margin = new Padding(0, 0, 0, (int)(14 * _s)) };
        card.Dock = DockStyle.Fill;
        card.Margin = Padding.Empty;
        holder.Controls.Add(card);
        return holder;
    }

    private Label StatTile(TableLayoutPanel parent, int col, string title, Color accent)
    {
        var tile = new Panel
        {
            Dock = DockStyle.Fill, BackColor = CCard,
            Margin = new Padding(col == 0 ? 0 : (int)(7 * _s), 0, col == 2 ? 0 : (int)(7 * _s), 0),
        };
        tile.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(CLine);
            using var path = Rounded(new Rectangle(0, 0, tile.Width - 1, tile.Height - 1), (int)(12 * _s));
            g.DrawPath(pen, path);
        };
        tile.Controls.Add(new Label
        {
            Text = title, Left = Pad, Top = (int)(18 * _s), AutoSize = true,
            ForeColor = CSub, Font = F(FS_Body), BackColor = Color.Transparent,
        });
        var v = new Label
        {
            Text = "—", Left = Pad, Top = (int)(50 * _s), AutoSize = true,
            ForeColor = accent, Font = F(19f, FontStyle.Bold), BackColor = Color.Transparent,
        };
        tile.Controls.Add(v);
        parent.Controls.Add(tile, col, 0);
        return v;
    }

    private Label KV(Panel card, string name, ref int y)
    {
        card.Controls.Add(new Label
        {
            Text = name, Left = Pad, Top = y + (int)(5 * _s), AutoSize = false,
            Width = (int)(104 * _s), Height = (int)(22 * _s), AutoEllipsis = true,
            ForeColor = CSub, Font = F(FS_Body), BackColor = Color.Transparent,
        });
        var v = new Label
        {
            Text = "—", Left = (int)(118 * _s), Top = y, AutoSize = false,
            Height = (int)(23 * _s), Width = (int)(420 * _s),
            AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = CText, Font = F(FS_Value), BackColor = Color.Transparent, Tag = "kvval",
        };
        card.Controls.Add(v);
        card.Resize += (s2, e2) => FitTracked(card);
        y += RowH;
        return v;
    }

    /// <summary>宽度自适应：Tag=fillw 的控件宽度跟随卡片；Tag=kvval 的值标签占满剩余宽度（超长省略号）。</summary>
    private void FitTracked(Panel card)
    {
        try
        {
            int w = card.ClientSize.Width;
            if (w <= 0) return;
            foreach (Control c in card.Controls)
            {
                string k = c.Tag as string;
                if (k == "fillw") c.Width = Math.Max((int)(140 * _s), w - Pad * 2);
                else if (k == "kvval") c.Width = Math.Max((int)(80 * _s), w - (int)(118 * _s) - Pad);
            }
        }
        catch { }
    }

    /* ---------- 对话页 ---------- */

    private Control BuildWebPage()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = CBg, AutoScroll = false };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = CBg };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58 * _s));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 50 * _s));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.Controls.Add(grid);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            BackColor = CBg, Padding = new Padding(0, (int)(4 * _s), 0, (int)(10 * _s)),
        };
        bar.Controls.Add(FlatBtn("重载网页", (s, e) => ReloadWeb()));
        bar.Controls.Add(FlatBtn("重新注入脚本", (s, e) => { WebBridge.I.InjectBridge(); WebBridge.I.Probe(); Toast("已重新注入 bridge.js"); }));
        bar.Controls.Add(FlatBtn("新建对话", (s, e) => NewChat()));
        bar.Controls.Add(FlatBtn("外部浏览器打开", (s, e) => OpenUrl(DsUrl)));
        bar.Controls.Add(new Label { Text = "网页缩放", AutoSize = true, ForeColor = CSub, Font = F(10.5f), Margin = new Padding((int)(14 * _s), (int)(12 * _s), (int)(6 * _s), 0), BackColor = Color.Transparent });
        bar.Controls.Add(FlatBtn("−", (s, e) => Zoom(-0.1)));
        _zoomText = new Label { Text = "100%", AutoSize = true, ForeColor = CText, Font = F(FS_Value), Margin = new Padding((int)(4 * _s), (int)(12 * _s), (int)(4 * _s), 0), BackColor = Color.Transparent };
        bar.Controls.Add(_zoomText);
        bar.Controls.Add(FlatBtn("+", (s, e) => Zoom(0.1)));
        grid.Controls.Add(bar, 0, 0);

        _acctChips = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            BackColor = CBg, AutoScroll = true, Padding = new Padding(0, 0, 0, (int)(8 * _s)),
        };
        grid.Controls.Add(_acctChips, 0, 1);

        _webHost = new Panel { Dock = DockStyle.Fill, BackColor = CCard, Padding = new Padding(1) };
        grid.Controls.Add(_webHost, 0, 2);

        _webHint = new Label
        {
            Dock = DockStyle.Fill, ForeColor = CSub, TextAlign = ContentAlignment.MiddleCenter,
            Font = F(FS_Body), Text = WebHintText + "\r\n\r\n正在初始化 WebView2…", BackColor = CCard,
        };
        _webHost.Controls.Add(_webHint);

        if (_web != null) { _webHost.Controls.Add(_web); _web.Dock = DockStyle.Fill; _web.BringToFront(); }
        return host;
    }

    private void Zoom(double delta)
    {
        try
        {
            if (_web == null) return;
            double z = _web.ZoomFactor + delta;
            if (z < 0.5) z = 0.5;
            if (z > 2.5) z = 2.5;
            _web.ZoomFactor = z;
            if (_zoomText != null) _zoomText.Text = Math.Round(z * 100) + "%";
        }
        catch { }
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
            try { _webHint.Text = "正在加载 DeepSeek 官网…"; _webHint.Visible = true; _webHint.BringToFront(); } catch { }
            _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = CCard };
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
                if (ev.IsSuccess)
                {
                    try { _webHint.Visible = false; } catch { }
                    WebBridge.I.InjectBridge();
                    WebBridge.I.Probe();
                }
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

            var primary = AccountPool.I.Find("primary");
            if (primary != null) { primary.Bridge = WebBridge.I; primary.View = _web; }
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
            if (_web != null) _web.Visible = false;
            _webHint.Text = text;
            _webHint.Visible = true;
            _webHint.BringToFront();
        }
        catch { }
    }

    private void ShowWebUnavailable()
    {
        ShowWebHint("未检测到 Microsoft Edge WebView2 运行时。\r\n\r\n" +
                    "本程序用 WebView2 驱动 DeepSeek 官网（HTTP 接口仍在运行，但调用会返回 503）。\r\n" +
                    "请安装「Evergreen 运行时」后重启本程序。\r\n\r\n" +
                    "Win11 / 新版 Win10 通常已内置。\r\n\r\n" +
                    "下载页：https://developer.microsoft.com/microsoft-edge/webview2/");
    }

    /* ================= 托盘 / 单实例 ================= */

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
        menu.Items.Add("复制 Base URL", null, (s, e) => Copy(BaseUrl(true)));
        menu.Items.Add("复制 API Key", null, (s, e) => Copy(Prefs.ApiKey));
        menu.Items.Add("新建对话", null, (s, e) => NewChat());
        menu.Items.Add("重载网页", null, (s, e) => ReloadWeb());
        menu.Items.Add("多账号轮换（开/关）", null, (s, e) => ToggleRotate());
        menu.Items.Add("新建账号窗口", null, (s, e) => AddAccountInteractive());
        var scale = new ToolStripMenuItem("界面缩放");
        foreach (var pct in new[] { 100, 115, 130, 150 })
        {
            int v = pct;
            var item = new ToolStripMenuItem(v + "%", null, (s, e) => ApplyScale(v)) { Checked = (int)(_s * 100) == v };
            scale.DropDownItems.Add(item);
        }
        menu.Items.Add(scale);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("启动服务", null, (s, e) => { ChatEngine.I.StartAll(Prefs.Port); RefreshStats(); });
        menu.Items.Add("停止服务", null, (s, e) => { ChatEngine.I.StopAll(); RefreshStats(); });
        menu.Items.Add("打开设置…", null, (s, e) => { ShowFromTray(); ShowSettings(); });
        menu.Items.Add("打开日志", null, (s, e) => OpenFile(Log.FilePath));
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

    private void ApplyScale(int pct)
    {
        try
        {
            Prefs.UiScalePct = pct;
            _s = pct / 100f;
            // 重建界面，但保留 WebView2 实例（重新挂载，不丢失登录态）
            if (_web != null && _web.Parent != null) _web.Parent.Controls.Remove(_web);
            SuspendLayout();
            var old = new List<Control>();
            foreach (Control c in Controls) old.Add(c);
            Controls.Clear();
            foreach (var c in old) { try { c.Dispose(); } catch { } }
            BuildUi();
            ResumeLayout(true);
            _content?.PerformLayout();
            Invalidate(true);
            Toast("界面缩放已切换为 " + pct + "%");
        }
        catch (Exception e) { Log.Write("切换界面缩放失败: " + e.Message); }
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

    /* ================= 定时刷新 ================= */

    private void OnTick(object sender, EventArgs e)
    {
        _tick++;
        if (_tick % 5 == 0) WebBridge.I.Probe();
        if (_tick % 30 == 0 && WebBridge.I.IsAttached) WebBridge.I.InjectBridge();
        RefreshStats();
        RefreshAccounts();
    }

    private void RefreshStats()
    {
        try
        {
            var eng = ChatEngine.I;
            bool bridgeOk = WebBridge.I.BridgeLoaded;

            if (_vState != null)
                FitTile(_vState, eng.State,
                    eng.State.StartsWith("运行中（已就绪") ? COk
                    : eng.State.StartsWith("运行中") ? CWarn
                    : eng.State == "已停止" ? CBad : CText);
            if (_vWeb != null)
                FitTile(_vWeb, !WebBridge.I.IsAttached ? "未初始化"
                    : !bridgeOk ? "桥接脚本缺失"
                    : (WebBridge.I.IsReady ? "已加载" : "加载中…"),
                    WebBridge.I.IsReady && bridgeOk ? COk : CWarn);
            if (_vLogin != null)
                FitTile(_vLogin, eng.LoggedIn ? "已登录" : "未登录", eng.LoggedIn ? COk : CBad);
            if (_vCalls != null) _vCalls.Text = eng.TotalCallsText + (eng.Inflight > 0 ? "，进行中 " + eng.Inflight : "");
            if (_vCtx != null) _vCtx.Text = eng.ContextInfoText;
            if (_vLast != null) _vLast.Text = eng.LastCallInfo;
            if (_vBase != null) _vBase.Text = "http://127.0.0.1:" + eng.Port + "/v1";
            if (_vLan != null) _vLan.Text = string.IsNullOrEmpty(eng.LanUrl) ? "—" : eng.LanUrl;
            if (_vKey != null) _vKey.Text = Prefs.ApiKey;
            if (_sideState != null)
                _sideState.Text = "服务 " + (eng.ServiceRunning ? "运行中" : "已停止")
                    + " · 端口 " + (eng.Port > 0 ? eng.Port : Prefs.Port) + "\r\n"
                    + "局域网 " + (Prefs.LanEnabled ? "开" : "关")
                    + " · 轮换 " + (Prefs.RotateEnabled ? "第 " + AccountPool.I.CycleNo + " 轮" : "关");
            if (_tray != null && _tray.Visible)
            {
                _tray.Text = Program.AppName + " · " + (eng.LanUrl.Length > 0 ? eng.LanUrl : ("127.0.0.1:" + eng.Port));
                if (_trayState != null) _trayState.Text = "状态：" + eng.State + " · " + eng.TotalCallsText;
            }
        }
        catch { }
    }

    /* ================= 行为 ================= */

    private string BaseUrl(bool local)
    {
        if (!local && Prefs.LanEnabled && ChatEngine.I.LanUrl.Length > 0) return ChatEngine.I.LanUrl;
        return "http://127.0.0.1:" + (ChatEngine.I.Port > 0 ? ChatEngine.I.Port : Prefs.Port) + "/v1";
    }

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
            if (_web?.CoreWebView2 != null)
            {
                try { _web.Visible = true; } catch { }
                _web.CoreWebView2.Navigate(DsUrl);
                Toast("正在重载官网…");
            }
            else { _webTried = false; _ = InitWebAsync(); }
        }
        catch (Exception e) { Toast("重载失败：" + e.Message); }
    }

    private void ShowSettings()
    {
        using var dlg = new SettingsDialog(_s);
        dlg.ShowDialog(this);
        RefreshStats();
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
        try
        {
            if (_toast != null)
            {
                _toast.Text = "• " + msg;
                _toast.Visible = true;
                _toast.BringToFront();
                _toastTimer?.Stop();
                _toastTimer?.Start();
            }
        }
        catch { }
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

    /* ================= 账号 / 轮换 ================= */

    /// <summary>把某个账号的 WebView2 实例挂起来（非主账号；各自独立数据目录 = 独立登录态与设备指纹）。</summary>
    private async Task EnsureSlotViewAsync(AccountSlot slot)
    {
        if (slot == null || slot.IsPrimary) return;
        if (slot.View is WebView2) return;
        if (_webHost == null) return;
        try
        {
            var wv = new WebView2 { Dock = DockStyle.Fill, Visible = false };
            _webHost.Controls.Add(wv);
            slot.View = wv;

            var bridge = new WebBridge { Name = slot.Name, BridgeJs = WebBridge.I.BridgeJs };
            slot.Bridge = bridge;

            string udf = slot.DataDir;
            Directory.CreateDirectory(udf);
            var env = await CoreWebView2Environment.CreateAsync(null, udf);
            await wv.EnsureCoreWebView2Async(env);
            var core = wv.CoreWebView2;
            try { core.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"; } catch { }
            core.Settings.IsStatusBarEnabled = false;

            core.WebMessageReceived += (s2, e2) =>
            {
                try { bridge.OnWebMessage(e2.TryGetWebMessageAsString()); } catch (Exception ex) { Log.Write("账号消息处理失败: " + ex.Message); }
            };
            bridge.SetStatusListener(new StatusForwarder(this, slot));
            core.NavigationCompleted += (s2, e2) =>
            {
                bridge.MarkPageLoaded(e2.IsSuccess);
                Log.Write("账号 " + slot.Name + " 页面加载 " + (e2.IsSuccess ? "成功" : "失败"));
                if (e2.IsSuccess) { bridge.InjectBridge(); bridge.Probe(); }
                RefreshAccounts();
            };
            core.ProcessFailed += (s2, e2) => { bridge.Detach(); slot.Note = "进程异常"; Log.Write("账号 " + slot.Name + " WebView2 进程异常"); };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebBridge.ShimJs);
            bridge.Attach(core);
            core.Navigate(DsUrl);
            Log.Write("账号 " + slot.Name + " 实例已创建 · 数据目录 " + udf);
        }
        catch (Exception e)
        {
            Log.Write("创建账号实例失败(" + slot.Name + "): " + e.Message);
            slot.Note = "创建失败";
        }
    }

    private async void SelectAccount(string id)
    {
        try
        {
            var slot = AccountPool.I.Find(id);
            if (slot == null) return;
            _currentAccountId = id;
            SwitchPage(1);
            if (!slot.IsPrimary) await EnsureSlotViewAsync(slot);
            foreach (var s2 in AccountPool.I.Snapshot())
                if (s2.View is WebView2 wv2) wv2.Visible = (s2.Id == id);
            try { if (_webHint != null && slot.LoggedIn) _webHint.Visible = false; } catch { }
            try { if (slot.View is WebView2 v2 && v2.CoreWebView2 != null) { slot.Bridge?.InjectBridge(); slot.Bridge?.Probe(); } } catch { }
        }
        catch (Exception e) { Log.Write("切换账号失败: " + e.Message); }
        RefreshAccounts();
    }

    private async void AddAccountInteractive()
    {
        try
        {
            int n = AccountPool.I.Snapshot().Count;
            string name = InputBox.Show(this, "新建账号（输入备注名）", "账号" + (n + 1));
            if (name == null) return;
            var slot = AccountPool.I.Add(string.IsNullOrWhiteSpace(name) ? ("账号" + (n + 1)) : name.Trim());
            await EnsureSlotViewAsync(slot);
            SelectAccount(slot.Id);
            Toast("已新增账号「" + slot.Name + "」，请在这个窗口登录 DeepSeek 官网");
        }
        catch (Exception e) { Log.Write("新建账号失败: " + e.Message); }
    }

    private void RemoveAccount(AccountSlot slot)
    {
        try
        {
            if (slot == null || slot.IsPrimary) { Toast("主账号不可删除"); return; }
            if (MessageBox.Show(this, "确定移除账号「" + slot.Name + "」？\n\n只从轮换列表移除，不删除它的本地数据（" + slot.DataDir + "）。",
                Program.AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            if (slot.View is WebView2 wv) { try { _webHost.Controls.Remove(wv); wv.Dispose(); } catch { } }
            AccountPool.I.Remove(slot.Id);
            if (_currentAccountId == slot.Id) _currentAccountId = "primary";
            RefreshAccounts();
        }
        catch (Exception e) { Log.Write("删除账号失败: " + e.Message); }
    }

    private async void ToggleRotate()
    {
        try
        {
            Prefs.RotateEnabled = !Prefs.RotateEnabled;
            Toast(Prefs.RotateEnabled ? "多账号轮换已开启" : "多账号轮换已关闭（回到单账号模式）");
            if (Prefs.RotateEnabled)
            {
                foreach (var sl in AccountPool.I.Snapshot().Where(x => x.Enabled && !x.IsPrimary))
                    await EnsureSlotViewAsync(sl);
            }
            RefreshAccounts();
            RefreshStats();
        }
        catch (Exception e) { Log.Write("切换轮换失败: " + e.Message); }
    }

    /// <summary>账号状态的紧凑写法（chip 上用）。</summary>
    private static string CompactState(AccountSlot s)
    {
        if (!s.Enabled) return "已停用";
        if (s.InCooldown) return "冷却 " + AccountSlot.FmtLeft(s.CooldownLeftSec);
        if (!s.PageReady) return "加载中";
        if (!s.LoggedIn) return "未登录";
        return s.SentInSession + "/" + Prefs.SessionSendLimit;
    }

    /// <summary>刷新侧栏账号列表 / 对话页 chip / 控制台状态（每秒调用，尽量只改文本不重建控件）。</summary>
    private void RefreshAccounts()
    {
        try
        {
            var slots = AccountPool.I.Snapshot();
            var order = slots.Select(x => x.Id).ToList();
            bool rebuild = !order.SequenceEqual(_acctOrder);
            if (rebuild)
            {
                _acctOrder = order;
                _acctBtns.Clear();
                _chipBtns.Clear();
                try { _acctFlow?.Controls.Clear(); } catch { }
                try { _acctChips?.Controls.Clear(); } catch { }
                foreach (var sl in slots)
                {
                    var b = new Button
                    {
                        Text = "● " + sl.Name, Width = Math.Max((int)(180 * _s), (_acctFlow?.ClientSize.Width ?? (int)(240 * _s)) - (int)(8 * _s)),
                        Height = (int)(54 * _s), FlatStyle = FlatStyle.Flat, BackColor = CSide, ForeColor = CText,
                        Font = F(FS_Body), TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand,
                        Margin = new Padding(0, 0, 0, (int)(4 * _s)),
                        Padding = new Padding((int)(10 * _s), 0, 0, 0), UseVisualStyleBackColor = false,
                    };
                    b.FlatAppearance.BorderSize = 0;
                    b.FlatAppearance.MouseOverBackColor = CAccentDim;
                    var cap = sl;
                    b.Click += (s2, e2) => SelectAccount(cap.Id);
                    var menu = new ContextMenuStrip();
                    menu.Items.Add("在对话页打开", null, (s2, e2) => SelectAccount(cap.Id));
                    menu.Items.Add("移除该账号", null, (s2, e2) => RemoveAccount(cap));
                    b.ContextMenuStrip = menu;
                    _acctFlow?.Controls.Add(b);
                    _acctBtns[sl.Id] = b;

                    var chip = new Button
                    {
                        Text = "● " + sl.Name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                        Height = (int)(40 * _s), FlatStyle = FlatStyle.Flat, BackColor = CCard, ForeColor = CText,
                        Font = F(FS_Body), Cursor = Cursors.Hand,
                        Margin = new Padding(0, 0, (int)(8 * _s), 0),
                        Padding = new Padding((int)(14 * _s), 0, (int)(14 * _s), 0), UseVisualStyleBackColor = false,
                    };
                    chip.FlatAppearance.BorderColor = CLine;
                    chip.FlatAppearance.MouseOverBackColor = CAccentDim;
                    chip.Click += (s2, e2) => SelectAccount(cap.Id);
                    chip.ContextMenuStrip = menu;
                    _acctChips?.Controls.Add(chip);
                    _chipBtns[sl.Id] = chip;
                }
                var addChip = FlatBtn("＋ 添加账号", (s2, e2) => AddAccountInteractive());
                addChip.Height = (int)(40 * _s);
                addChip.MinimumSize = Size.Empty;
                _acctChips?.Controls.Add(addChip);
                BuildAcctTable(slots);
            }

            foreach (var sl in slots)
            {
                string dot = !sl.Enabled ? "○" : sl.InCooldown ? "⏳" : (!sl.PageReady ? "◌" : (sl.LoggedIn ? "●" : "○"));
                if (_acctBtns.TryGetValue(sl.Id, out var b))
                {
                    b.Text = dot + "  " + sl.Name + "\r\n     " + sl.StateText;
                    b.ForeColor = sl.Id == _currentAccountId ? CAccent : CText;
                    b.BackColor = sl.Id == _currentAccountId ? CAccentDim : CSide;
                }
                if (_chipBtns.TryGetValue(sl.Id, out var c))
                {
                    c.Text = dot + " " + sl.Name + " · " + CompactState(sl);
                    c.ForeColor = sl.Id == _currentAccountId ? CAccent : CText;
                    c.BackColor = sl.Id == _currentAccountId ? CAccentDim : CCard;
                }
            }
            UpdateAcctTable(slots);

            if (_vRotate != null)
                _vRotate.Text = (Prefs.RotateEnabled ? "已开启" : "已关闭") + " · " + AccountPool.I.SummaryText()
                    + (Prefs.RotateEnabled ? " · 下一棒 " + AccountPool.I.NextName() : "");
            if (_acctHead != null)
                _acctHead.Text = Prefs.RotateEnabled ? ("第 " + AccountPool.I.CycleNo + " 轮") : "未开启";
        }
        catch { }
    }

    /* ---------- 控制台「账号调用情况」表 ---------- */
    private void BuildAcctTable(List<AccountSlot> slots)
    {
        if (_acctTable == null) return;
        var olds = new List<Control>();
        foreach (Control c in _acctTable.Controls) olds.Add(c);
        foreach (var c in olds) { try { c.Dispose(); } catch { } }
        _acctTable.Controls.Clear();
        _acctTable.RowStyles.Clear();
        _tblRows.Clear();

        string[] heads = { "#", "账号", "状态", "本轮已发", "累计调用", "最近使用" };
        _acctTable.RowCount = slots.Count + 1;
        for (int i = 0; i < 6; i++)
            _acctTable.Controls.Add(new Label
            {
                Text = heads[i], Dock = DockStyle.Fill, ForeColor = CSub, Font = F(10.5f),
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, Margin = new Padding(0, 0, (int)(6 * _s), 0),
            }, i, 0);
        _acctTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 32 * _s));

        for (int r = 0; r < slots.Count; r++)
        {
            var set = new Label[6];
            for (int c = 0; c < 6; c++)
            {
                var lb = new Label
                {
                    Text = "—", Dock = DockStyle.Fill, ForeColor = c == 1 ? CText : CSub,
                    Font = F(c == 1 ? FS_Value : FS_Body), TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, (int)(6 * _s), 0),
                };
                _acctTable.Controls.Add(lb, c, r + 1);
                set[c] = lb;
            }
            _tblRows[slots[r].Id] = set;
            _acctTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 34 * _s));
        }
    }

    private void UpdateAcctTable(List<AccountSlot> slots)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (!_tblRows.TryGetValue(s.Id, out var set)) continue;
            bool active = s.Id == _currentAccountId;
            set[0].Text = (i + 1).ToString();
            set[1].Text = s.Name + (s.IsPrimary ? "（主）" : "");
            set[1].ForeColor = active ? CAccent : CText;
            string st; Color sc;
            if (!s.Enabled) { st = "已停用"; sc = CSub; }
            else if (s.InCooldown) { st = "冷却 " + AccountSlot.FmtLeft(s.CooldownLeftSec); sc = CBad; }
            else if (!s.PageReady) { st = "加载中…"; sc = CSub; }
            else if (!s.LoggedIn) { st = "未登录"; sc = CWarn; }
            else { st = s.NeedNewChat ? "就绪 · 待新开对话" : "就绪"; sc = COk; }
            set[2].Text = st; set[2].ForeColor = sc;
            set[3].Text = s.SentInSession + " / " + Prefs.SessionSendLimit;
            set[4].Text = s.TotalCalls + " 次" + (s.RateLimitedCount > 0 ? "（限流 " + s.RateLimitedCount + "）" : "");
            set[5].Text = s.LastUsedMs > 0 ? Ago(s.LastUsedMs) : "—";
        }
    }

    private static string Ago(long ms)
    {
        double sec = (AccountSlot.NowMs - ms) / 1000.0;
        if (sec < 60) return "刚刚";
        if (sec < 3600) return (int)(sec / 60) + " 分钟前";
        if (sec < 86400) return (int)(sec / 3600) + " 小时前";
        return (int)(sec / 86400) + " 天前";
    }

    /* ---------- 深色标题栏（去掉顶部白边） ---------- */
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkChrome();
    }

    private void ApplyDarkChrome()
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));   // 旧版编号
            int caption = ColorTranslator.ToWin32(CBg);
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
            int txt = ColorTranslator.ToWin32(CText);
            DwmSetWindowAttribute(Handle, 36, ref txt, sizeof(int));     // DWMWA_TEXT_COLOR
            int border = ColorTranslator.ToWin32(CBg);
            DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));  // DWMWA_BORDER_COLOR
            int round = 2;
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));   // 圆角
        }
        catch { }
    }

    /* ---------- 连发次数 / 检查更新 ---------- */
    private void EditSendLimit()
    {
        try
        {
            string v = InputBox.Show(this, "每个对话最多连发几次（1-50）", Prefs.SessionSendLimit.ToString());
            if (v == null) return;
            if (!int.TryParse(v.Trim(), out int n) || n < 1 || n > 50) { Toast("请输入 1-50 之间的整数"); return; }
            Prefs.SessionSendLimit = n;
            Toast("已设置：每个账号的每个对话连发 " + n + " 次后换下一个账号");
            RefreshAccounts(); RefreshStats();
        }
        catch (Exception e) { Log.Write("设置连发次数失败: " + e.Message); }
    }

    private async void CheckUpdate()
    {
        Toast("正在检查更新…");
        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.Add("User-Agent", "DeepSeekWebAPI-Windows");
            string json = await http.GetStringAsync("https://api.github.com/repos/Hiweny/deepseek-web-api/releases/latest");
            var o = Json.TryParse(json) as JObj;
            string tag = o == null ? "" : o.Str("tag_name", "");
            string url = o == null ? "" : o.Str("html_url", "");
            if (string.IsNullOrEmpty(tag)) { Toast("检查更新失败：未取到版本信息"); return; }
            var m = System.Text.RegularExpressions.Regex.Match(tag, @"(\d+)\.(\d+)\.(\d+)");
            string latest = m.Success ? m.Value : tag;
            if (CmpVer(latest, Program.Version) <= 0)
            {
                Toast("已是最新版（v" + Program.Version + "）");
                return;
            }
            var r = MessageBox.Show(this,
                "发现新版本：" + tag + "\n当前版本：v" + Program.Version + "\n\n是否打开下载页面？\n\n升级方式：下载新版 exe，关闭本程序后直接覆盖旧文件即可（登录态与设置在 %LOCALAPPDATA%\\DeepSeekWebAPI，不受影响）。",
                Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
                OpenUrl(string.IsNullOrEmpty(url) ? "https://github.com/Hiweny/deepseek-web-api/releases/latest" : url);
        }
        catch (Exception e) { Toast("检查更新失败：" + e.Message); }
    }

    private static int CmpVer(string a, string b)
    {
        var x = (a ?? "").Split('.');
        var y = (b ?? "").Split('.');
        for (int i = 0; i < 3; i++)
        {
            int.TryParse(i < x.Length ? x[i] : "0", out int vi);
            int.TryParse(i < y.Length ? y[i] : "0", out int vj);
            if (vi != vj) return vi < vj ? -1 : 1;
        }
        return 0;
    }

    /// <summary>把 bridge 状态转发给引擎（UI 线程）。</summary>
    private sealed class StatusForwarder : WebBridge.IStatusListener
    {
        private readonly MainForm _f;
        private readonly AccountSlot _slot;
        public StatusForwarder(MainForm f, AccountSlot slot) { _f = f; _slot = slot; }
        public void OnStatus(string json)
        {
            ChatEngine.I.OnStatus(json);
            if (_slot != null) ChatEngine.I.OnSlotStatus(_slot, json);
            try
            {
                if (_f.IsHandleCreated && !_f.IsDisposed)
                    _f.BeginInvoke(new Action(() => { _f.RefreshAccounts(); _f.RefreshStats(); }));
            }
            catch { }
        }
    }
}
