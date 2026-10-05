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
    private static readonly Color CBg = Color.FromArgb(0x0D, 0x10, 0x15);
    private static readonly Color CSide = Color.FromArgb(0x11, 0x15, 0x1C);
    private static readonly Color CCard = Color.FromArgb(0x17, 0x1C, 0x24);
    private static readonly Color CLine = Color.FromArgb(0x2A, 0x31, 0x3E);
    private static readonly Color CText = Color.FromArgb(0xEA, 0xED, 0xF3);
    private static readonly Color CSub = Color.FromArgb(0x93, 0x9E, 0xAF);
    private static readonly Color CAccent = Color.FromArgb(0x5B, 0x96, 0xFF);
    private static readonly Color CAccentDim = Color.FromArgb(0x1B, 0x26, 0x3E);
    private static readonly Color COk = Color.FromArgb(0x3D, 0xD6, 0x8C);
    private static readonly Color CWarn = Color.FromArgb(0xFF, 0xB4, 0x4D);
    private static readonly Color CBad = Color.FromArgb(0xFF, 0x6B, 0x6B);

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
        WebBridge.I.SetStatusListener(new StatusForwarder(this));

        _toastTimer = new System.Windows.Forms.Timer { Interval = 3200 };
        _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); try { if (_toast != null) _toast.Visible = false; } catch { } };

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
            MinimumSize = new Size((int)(104 * _s), (int)(42 * _s)),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? CAccentDim : CCard,
            ForeColor = primary ? CAccent : CText,
            Font = F(11.5f),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, (int)(12 * _s), 0),
            Padding = new Padding((int)(10 * _s), 0, (int)(10 * _s), 0),
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = primary ? CAccent : CLine;
        b.FlatAppearance.MouseOverBackColor = CAccentDim;
        if (onClick != null) b.Click += onClick;
        return b;
    }

    /// <summary>圆角卡片容器（统一外观）。</summary>
    private Panel Card(string title, int height)
    {
        var card = new Panel
        {
            BackColor = CCard,
            Height = (int)(height * _s),
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, (int)(14 * _s)),
        };
        card.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(CLine);
            using var path = Rounded(new Rectangle(0, 0, card.Width - 1, card.Height - 1), (int)(12 * _s));
            g.DrawPath(pen, path);
        };
        if (title != null)
        {
            card.Controls.Add(new Label
            {
                Text = title, Left = (int)(22 * _s), Top = (int)(18 * _s), AutoSize = true,
                ForeColor = CAccent, Font = F(13f, FontStyle.Bold), BackColor = Color.Transparent,
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
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 282 * _s));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        /* ---- 侧边栏 ---- */
        var side = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = CSide, Margin = Padding.Empty,
        };
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 122 * _s));
        side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        side.RowStyles.Add(new RowStyle(SizeType.Absolute, 152 * _s));
        root.Controls.Add(side, 0, 0);

        var head = new Panel { Dock = DockStyle.Fill, BackColor = CSide };
        try
        {
            var ico = Icon;
            if (ico != null)
                head.Controls.Add(new PictureBox
                {
                    Left = (int)(24 * _s), Top = (int)(32 * _s), Width = (int)(44 * _s), Height = (int)(44 * _s),
                    SizeMode = PictureBoxSizeMode.Zoom, Image = ico.ToBitmap(), BackColor = Color.Transparent,
                });
        }
        catch { }
        head.Controls.Add(new Label
        {
            Text = "DeepSeek Web API", Left = (int)(80 * _s), Top = (int)(30 * _s), AutoSize = true,
            ForeColor = CText, Font = F(15f, FontStyle.Bold), BackColor = Color.Transparent,
        });
        head.Controls.Add(new Label
        {
            Text = "Windows 桌面版 v" + Program.Version, Left = (int)(80 * _s), Top = (int)(64 * _s), AutoSize = true,
            ForeColor = CSub, Font = F(10.5f), BackColor = Color.Transparent,
        });
        side.Controls.Add(head, 0, 0);

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = CSide, Padding = new Padding((int)(14 * _s), (int)(6 * _s), (int)(14 * _s), 0),
        };
        side.Controls.Add(nav, 0, 1);
        AddNav(nav, "\uD83C\uDF9B   控制台", 0);
        AddNav(nav, "\uD83D\uDCAC   对话页", 1);

        var foot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = CSide,
            Padding = new Padding((int)(18 * _s), 0, (int)(18 * _s), (int)(14 * _s)),
        };
        foot.RowCount = 4;
        foot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 40 * _s));
        foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 48 * _s));
        foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 48 * _s));
        _sideState = new Label
        {
            Dock = DockStyle.Fill, ForeColor = CSub, Font = F(10.5f), BackColor = Color.Transparent,
            Text = "服务：—",
        };
        foot.Controls.Add(_sideState, 0, 1);
        var stBtn = FlatBtn("设置…", (s, e) => ShowSettings());
        stBtn.Dock = DockStyle.Fill; stBtn.Margin = new Padding(0, 0, 0, (int)(8 * _s));
        foot.Controls.Add(stBtn, 0, 2);
        var exitBtn = FlatBtn("退出程序", (s, e) => { _reallyExit = true; Close(); });
        exitBtn.Dock = DockStyle.Fill; exitBtn.Margin = Padding.Empty;
        foot.Controls.Add(exitBtn, 0, 3);
        side.Controls.Add(foot, 0, 2);

        /* ---- 内容区 ---- */
        _content = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Padding = new Padding((int)(30 * _s), (int)(26 * _s), (int)(30 * _s), (int)(20 * _s)) };
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
            Width = (int)(254 * _s),
            Height = (int)(60 * _s),
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding((int)(18 * _s), 0, 0, 0),
            BackColor = CSide,
            ForeColor = CText,
            Font = F(13.5f),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, (int)(5 * _s), 0, (int)(5 * _s)),
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
            _navBtns[i].ForeColor = i == index ? CAccent : CText;
            _navBtns[i].Font = F(13.5f, i == index ? FontStyle.Bold : FontStyle.Regular);
        }
        if (index == 1 && !_webTried) _ = InitWebAsync();
        RefreshStats();
    }

    /* ---------- 控制台页 ---------- */

    private Control BuildConsolePage()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = CBg, AutoScroll = true };
        // 注意：TableLayoutPanel 一旦 AutoSize，Dock/Width 会被它自己重算，导致「内容不铺满窗口」。
        // 这里用 MaximumSize.Width 强制列宽随窗口变化，高度仍由内容决定。
        var inner = new TableLayoutPanel
        {
            // AutoSize 必须为 false：TableLayoutPanel 一旦 AutoSize，就会用「自身首选宽度」覆盖外部设定的宽度，
            // 表现就是窗口放大后内容不铺满（右侧留一大片空白）。这里宽度由窗口决定，高度按内容算。
            AutoSize = false, Location = new Point(0, 0),
            ColumnCount = 1, BackColor = CBg, Margin = Padding.Empty,
        };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.Controls.Add(inner);

        void Sync()
        {
            try
            {
                // 注意：AutoScroll 面板的 ClientSize 已经扣除了滚动条宽度，这里不能再减一次，
                // 否则右边会多出一条空白（实测约 -47px）。只留 8px 余量。
                int w = Math.Max((int)(640 * _s), host.ClientSize.Width - 8);
                inner.Width = w;
                inner.Height = inner.GetPreferredSize(new Size(w, 0)).Height;
            }
            catch { }
        }
        host.HandleCreated += (s, e) => Sync();
        host.ClientSizeChanged += (s, e) => Sync();

        inner.Controls.Add(new Label
        {
            Text = "控制台", AutoSize = true, ForeColor = CText, Font = F(24f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, (int)(6 * _s)), BackColor = Color.Transparent,
        });
        inner.Controls.Add(new Label
        {
            Text = "把 chat.deepseek.com 官网封装成本机 / 局域网的 OpenAI 兼容接口",
            AutoSize = true, ForeColor = CSub, Font = F(11.5f),
            Margin = new Padding(0, 0, 0, (int)(20 * _s)), BackColor = Color.Transparent,
        });

        /* 三块状态磁贴 */
        var tiles = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 1, Dock = DockStyle.Top,
            Height = (int)(118 * _s), BackColor = CBg, Margin = new Padding(0, 0, 0, (int)(16 * _s)),
        };
        for (int i = 0; i < 3; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        tiles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _vState = StatTile(tiles, 0, "服务状态", CWarn);
        _vWeb = StatTile(tiles, 1, "网页 / 桥接", CWarn);
        _vLogin = StatTile(tiles, 2, "登录状态", CWarn);
        inner.Controls.Add(tiles);

        /* 接口信息：3 行键值 + 一行按钮（按钮放在最后一行下方，避免与 API Key 重叠） */
        var c1 = Card("接口信息", 224);
        int y = (int)(58 * _s);
        _vBase = KV(c1, "Base URL（本机）", ref y);
        _vLan = KV(c1, "Base URL（局域网）", ref y);
        _vKey = KV(c1, "API Key", ref y);
        var b1 = new FlowLayoutPanel
        {
            Left = (int)(22 * _s), Top = y + (int)(10 * _s), Height = (int)(44 * _s),
            Width = (int)(960 * _s), BackColor = Color.Transparent, WrapContents = false,
        };
        b1.Controls.Add(FlatBtn("复制 Base URL", (s2, e2) => Copy(BaseUrl(true)), true));
        b1.Controls.Add(FlatBtn("复制 API Key", (s2, e2) => Copy(Prefs.ApiKey)));
        b1.Controls.Add(FlatBtn("复制局域网地址", (s2, e2) => Copy(BaseUrl(false))));
        c1.Controls.Add(b1);
        inner.Controls.Add(Wrap(c1));

        /* 运行数据 */
        var c2 = Card("运行数据", 172);
        y = (int)(58 * _s);
        _vCalls = KV(c2, "调用统计", ref y);
        _vCtx = KV(c2, "会话上下文", ref y);
        _vLast = KV(c2, "最近一次", ref y);
        inner.Controls.Add(Wrap(c2));

        /* 常用操作（两行按钮，行距固定，互不重叠） */
        var c3 = Card("常用操作", 178);
        var b3 = new FlowLayoutPanel
        {
            Left = (int)(22 * _s), Top = (int)(58 * _s), Height = (int)(46 * _s),
            Width = (int)(960 * _s), BackColor = Color.Transparent, WrapContents = false,
        };
        b3.Controls.Add(FlatBtn("新建对话", (s2, e2) => NewChat()));
        b3.Controls.Add(FlatBtn("重载网页", (s2, e2) => ReloadWeb()));
        b3.Controls.Add(FlatBtn("放行防火墙（需管理员）", (s2, e2) => AddFirewallRule()));
        b3.Controls.Add(FlatBtn("复制 netsh 命令", (s2, e2) => { Copy(FirewallCmd()); Toast("netsh 命令已复制"); }));
        var b3b = new FlowLayoutPanel
        {
            Left = (int)(22 * _s), Top = (int)(112 * _s), Height = (int)(46 * _s),
            Width = (int)(960 * _s), BackColor = Color.Transparent, WrapContents = false,
        };
        b3b.Controls.Add(FlatBtn("打开设置", (s2, e2) => ShowSettings()));
        b3b.Controls.Add(FlatBtn("打开日志", (s2, e2) => OpenFile(Log.FilePath)));
        b3b.Controls.Add(FlatBtn("打开数据目录", (s2, e2) => OpenFolder(Log.Dir)));
        c3.Controls.Add(b3);
        c3.Controls.Add(b3b);
        inner.Controls.Add(Wrap(c3));

        /* 客户端填写说明 */
        var c4 = Card("客户端填写（OpenAI 兼容）", 248);
        c4.Controls.Add(new Label
        {
            Left = (int)(22 * _s), Top = (int)(58 * _s), Width = (int)(960 * _s), Height = (int)(108 * _s),
            ForeColor = CText, Font = F(12f), BackColor = Color.Transparent,
            Text = "Base URL : http://127.0.0.1:8787/v1      # 局域网设备换成上面的局域网地址\r\n" +
                   "API Key  : " + Prefs.ApiKey + "\r\n" +
                   "Model    : deepseek                     # 任意名称均可",
        });
        c4.Controls.Add(new Label
        {
            Left = (int)(22 * _s), Top = (int)(172 * _s), Width = (int)(960 * _s), Height = (int)(56 * _s),
            ForeColor = CSub, Font = F(10.5f), BackColor = Color.Transparent,
            Text = "局域网访问需放行防火墙（点上面的按钮或手动执行 netsh）；\r\n" +
                   "调用前请在「对话页」登录官网，未登录时接口返回 503。",
        });
        inner.Controls.Add(Wrap(c4));

        inner.Controls.Add(new Panel { Height = (int)(16 * _s), BackColor = CBg });
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
            Text = title, Left = (int)(24 * _s), Top = (int)(20 * _s), AutoSize = true,
            ForeColor = CSub, Font = F(11.5f), BackColor = Color.Transparent,
        });
        var v = new Label
        {
            Text = "—", Left = (int)(24 * _s), Top = (int)(54 * _s), AutoSize = true,
            ForeColor = accent, Font = F(18f, FontStyle.Bold), BackColor = Color.Transparent,
        };
        tile.Controls.Add(v);
        parent.Controls.Add(tile, col, 0);
        return v;
    }

    private Label KV(Panel card, string name, ref int y)
    {
        card.Controls.Add(new Label
        {
            Text = name, Left = (int)(22 * _s), Top = y + (int)(4 * _s), AutoSize = true,
            ForeColor = CSub, Font = F(11.5f), BackColor = Color.Transparent,
        });
        var v = new Label
        {
            Text = "—", Left = (int)(290 * _s), Top = y, AutoSize = true,
            ForeColor = CText, Font = F(12.5f), BackColor = Color.Transparent,
        };
        card.Controls.Add(v);
        y += (int)(32 * _s);
        return v;
    }

    /* ---------- 对话页 ---------- */

    private Control BuildWebPage()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = CBg, AutoScroll = false };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = CBg };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58 * _s));
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
        bar.Controls.Add(new Label { Text = "缩放", AutoSize = true, ForeColor = CSub, Font = F(10.5f), Margin = new Padding((int)(14 * _s), (int)(10 * _s), (int)(6 * _s), 0), BackColor = Color.Transparent });
        bar.Controls.Add(FlatBtn("−", (s, e) => Zoom(-0.1)));
        _zoomText = new Label { Text = "100%", AutoSize = true, ForeColor = CText, Font = F(10.5f), Margin = new Padding((int)(4 * _s), (int)(10 * _s), (int)(4 * _s), 0), BackColor = Color.Transparent };
        bar.Controls.Add(_zoomText);
        bar.Controls.Add(FlatBtn("+", (s, e) => Zoom(0.1)));
        grid.Controls.Add(bar, 0, 0);

        _webHost = new Panel { Dock = DockStyle.Fill, BackColor = CCard, Padding = new Padding(1) };
        grid.Controls.Add(_webHost, 0, 1);

        _webHint = new Label
        {
            Dock = DockStyle.Fill, ForeColor = CSub, TextAlign = ContentAlignment.MiddleCenter,
            Font = F(11f), Text = WebHintText + "\r\n\r\n正在初始化 WebView2…", BackColor = CCard,
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
                _sideState.Text = "服务：" + eng.State + "\r\n端口：" + (eng.Port > 0 ? eng.Port : Prefs.Port)
                    + " · 局域网：" + (Prefs.LanEnabled ? "开" : "关");
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
