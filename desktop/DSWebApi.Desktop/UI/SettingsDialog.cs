using DSWebApi.Desktop.Core;

namespace DSWebApi.Desktop.UI;

/// <summary>
/// 设置对话框（不占用标签页，与 APK 的「设置」弹窗对应）。
/// 只放必要项：端口 / API Key / 思考与搜索策略 / 无状态 / 上下文轮换 / 局域网 / 桌面行为 / 界面缩放。
/// </summary>
internal sealed class SettingsDialog : Form
{
    private static readonly Color CBg = Color.FromArgb(0x0D, 0x10, 0x15);
    private static readonly Color CCard = Color.FromArgb(0x17, 0x1C, 0x24);
    private static readonly Color CLine = Color.FromArgb(0x2A, 0x31, 0x3E);
    private static readonly Color CText = Color.FromArgb(0xEA, 0xED, 0xF3);
    private static readonly Color CSub = Color.FromArgb(0x93, 0x9E, 0xAF);
    private static readonly Color CAccent = Color.FromArgb(0x5B, 0x96, 0xFF);

    private readonly float _s;
    private Font F(float size, FontStyle st = FontStyle.Regular) =>
        new Font("Microsoft YaHei UI", Math.Max(6f, size * _s), st);

    public SettingsDialog(float scale)
    {
        _s = scale <= 0 ? 1f : scale;
        Text = "设置";
        BackColor = CBg;
        ForeColor = CText;
        Font = F(10.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size((int)(760 * _s), (int)(620 * _s));
        try { var ico = Icon.ExtractAssociatedIcon(Environment.ProcessPath); if (ico != null) Icon = ico; } catch { }

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = CBg, Padding = new Padding((int)(16 * _s)) };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, BackColor = CBg,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250 * _s));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(grid);
        scroll.ClientSizeChanged += (s2, e2) => { try { grid.Width = Math.Max((int)(420 * _s), scroll.ClientSize.Width - 2); } catch { } };

        Header(grid, "接口");
        TextRow(grid, "监听端口", () => Prefs.Port.ToString(), v => { if (int.TryParse(v, out var p) && p >= 1024 && p <= 65535) { Prefs.Port = p; Note("端口修改后重启程序生效"); } });
        TextRow(grid, "API Key", () => Prefs.ApiKey, v => Prefs.ApiKey = v.Trim());
        TextRow(grid, "单次调用超时（秒）", () => Prefs.TimeoutSec.ToString(), v => { if (int.TryParse(v, out var t) && t >= 10 && t <= 3600) Prefs.TimeoutSec = t; });
        BoolRow(grid, "允许局域网访问（0.0.0.0）", () => Prefs.LanEnabled, v => { Prefs.LanEnabled = v; Note("重启服务后生效"); });

        Header(grid, "内网穿透（Cloudflare Tunnel · 免费）");
        BoolRow(grid, "启用公网访问（固定域名）", () => Prefs.TunnelEnabled, v =>
        {
            Prefs.TunnelEnabled = v;
            if (v) CloudflareTunnel.I.StartAsync(); else CloudflareTunnel.I.Stop();
            Note(v ? "正在建立公网隧道，公网地址见主界面「接口信息」" : "已停止公网隧道");
        });
        TextRow(grid, "公网域名", () => Prefs.TunnelHostname, v =>
        {
            Prefs.TunnelHostname = v.Trim();
            Note("换了域名后：关一下再开「启用公网访问」即可重新绑定");
        });
        TextRow(grid, "Cloudflare API Token", () => Prefs.TunnelApiToken, v =>
        {
            Prefs.TunnelApiToken = v.Trim();
            Note("Token 只保存在本机设置文件，不会外传");
        });
        TextRow(grid, "cloudflared.exe 路径", () => Prefs.CloudflaredPath, v =>
        {
            Prefs.CloudflaredPath = v.Trim();
            Note("留空则在启用时自动下载到数据目录");
        });

        Header(grid, "对话策略");
        BoolRow(grid, "无状态模式（每轮不带历史）", () => Prefs.Stateless, v => Prefs.Stateless = v);
        ComboRow(grid, "思考模式", new[] { "auto", "on", "off" }, () => Prefs.ThinkingMode, v => Prefs.ThinkingMode = v);
        ComboRow(grid, "联网搜索", new[] { "auto", "on", "off" }, () => Prefs.SearchMode, v => Prefs.SearchMode = v);
        BoolRow(grid, "上下文达阈值自动新开对话", () => Prefs.AutoNewChat, v => Prefs.AutoNewChat = v);
        TextRow(grid, "上下文上限（tokens）", () => Prefs.ContextTokens.ToString(), v => { if (int.TryParse(v, out var t) && t >= 8000) Prefs.ContextTokens = t; });
        TextRow(grid, "新对话阈值（%）", () => Prefs.NewChatThreshold.ToString(), v => { if (int.TryParse(v, out var t) && t >= 10 && t <= 100) Prefs.NewChatThreshold = t; });
        TextRow(grid, "单次输入字符上限（0=不限）", () => Prefs.MaxPromptChars.ToString(), v => { if (int.TryParse(v, out var t) && t >= 0) { Prefs.MaxPromptChars = t; Note("超出时自动省略中段历史，保留系统指令与最近对话"); } });
        TextRow(grid, "图片附件数量上限（0=不限）", () => Prefs.MaxRefImages.ToString(), v => { if (int.TryParse(v, out var t) && t >= 0) { Prefs.MaxRefImages = t; Note("超出时只保留最近上传的 N 张图片"); } });

        Header(grid, "桌面行为");
        BoolRow(grid, "关闭窗口即最小化到托盘", () => Prefs.CloseToTray, v => Prefs.CloseToTray = v);
        BoolRow(grid, "开机自动启动（当前用户）", () => Prefs.AutoStart, v => { Prefs.AutoStart = v; AutoRun.Set(v, Environment.ProcessPath); });
        ComboRow(grid, "界面缩放", new[] { "100", "115", "130", "150" }, () => Prefs.UiScalePct.ToString(), v =>
        {
            Prefs.UiScalePct = int.Parse(v);
            Note("界面缩放在主界面托盘菜单里切换后会立即重建界面");
        });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = (int)(66 * _s), BackColor = CBg, Padding = new Padding((int)(16 * _s), (int)(10 * _s), (int)(16 * _s), (int)(14 * _s)) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = CBg };
        var close = new Button
        {
            Text = "完成", Width = (int)(120 * _s), Height = (int)(38 * _s), FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0x1B, 0x26, 0x3E), ForeColor = CAccent, Font = F(10.5f),
            Cursor = Cursors.Hand, DialogResult = DialogResult.OK,
        };
        close.FlatAppearance.BorderColor = CLine;
        flow.Controls.Add(close);
        var open = new Button
        {
            Text = "打开数据目录", AutoSize = true, Height = (int)(38 * _s), FlatStyle = FlatStyle.Flat,
            BackColor = CCard, ForeColor = CText, Font = F(10.5f), Cursor = Cursors.Hand,
            Padding = new Padding((int)(12 * _s), 0, (int)(12 * _s), 0),
        };
        open.FlatAppearance.BorderColor = CLine;
        open.Click += (s2, e2) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + Log.Dir + "\"") { UseShellExecute = true }); } catch { } };
        flow.Controls.Add(open);
        bottom.Controls.Add(flow);

        Controls.Add(scroll);
        Controls.Add(bottom);
        AcceptButton = close;
    }

    private void Note(string msg) => Log.Write("[设置] " + msg);

    private void Header(TableLayoutPanel grid, string text)
    {
        int r = grid.RowCount;
        grid.RowCount = r + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label
        {
            Text = text, AutoSize = true, ForeColor = CAccent, Font = F(12f, FontStyle.Bold),
            Margin = new Padding(0, (int)(14 * _s), 0, (int)(6 * _s)), BackColor = Color.Transparent,
        };
        grid.Controls.Add(l, 0, r);
        grid.SetColumnSpan(l, 2);
    }

    private void TextRow(TableLayoutPanel grid, string label, Func<string> get, Action<string> set)
    {
        var name = new Label { Text = label, AutoSize = true, ForeColor = CSub, Font = F(10.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, (int)(8 * _s), 0, (int)(8 * _s)), BackColor = Color.Transparent };
        var tb = new TextBox
        {
            Text = get() ?? "", Dock = DockStyle.Fill, BackColor = Color.FromArgb(0x0F, 0x13, 0x19), ForeColor = CText,
            BorderStyle = BorderStyle.FixedSingle, Font = F(10.5f), Margin = new Padding(0, (int)(6 * _s), 0, (int)(6 * _s)),
        };
        tb.Leave += (s, e) => { try { set(tb.Text); tb.Text = get() ?? ""; } catch { } };
        AddRow(grid, name, tb);
    }

    private void BoolRow(TableLayoutPanel grid, string label, Func<bool> get, Action<bool> set)
    {
        var name = new Label { Text = label, AutoSize = true, ForeColor = CSub, Font = F(10.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, (int)(8 * _s), 0, (int)(8 * _s)), BackColor = Color.Transparent };
        var cb = new CheckBox
        {
            Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
            Width = (int)(92 * _s), Height = (int)(30 * _s), Anchor = AnchorStyles.Left,
            Checked = get(), BackColor = get() ? Color.FromArgb(0x1B, 0x26, 0x3E) : CCard,
            ForeColor = get() ? CAccent : CSub, Text = get() ? "已开启" : "已关闭", Font = F(10f),
            Margin = new Padding(0, (int)(6 * _s), 0, (int)(6 * _s)),
        };
        cb.FlatAppearance.BorderColor = CLine;
        cb.CheckedChanged += (s, e) =>
        {
            try
            {
                set(cb.Checked);
                cb.Text = cb.Checked ? "已开启" : "已关闭";
                cb.BackColor = cb.Checked ? Color.FromArgb(0x1B, 0x26, 0x3E) : CCard;
                cb.ForeColor = cb.Checked ? CAccent : CSub;
            }
            catch { }
        };
        AddRow(grid, name, cb);
    }

    private void ComboRow(TableLayoutPanel grid, string label, string[] values, Func<string> get, Action<string> set)
    {
        var name = new Label { Text = label, AutoSize = true, ForeColor = CSub, Font = F(10.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, (int)(8 * _s), 0, (int)(8 * _s)), BackColor = Color.Transparent };
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = (int)(150 * _s), Anchor = AnchorStyles.Left,
            BackColor = CCard, ForeColor = CText, FlatStyle = FlatStyle.Flat, Font = F(10.5f),
            Margin = new Padding(0, (int)(6 * _s), 0, (int)(6 * _s)),
        };
        foreach (var v in values) cb.Items.Add(v);
        string cur = get() ?? "";
        int idx = Array.IndexOf(values, cur);
        cb.SelectedIndex = idx >= 0 ? idx : 0;
        cb.SelectedIndexChanged += (s, e) => { try { set(cb.SelectedItem.ToString()); } catch { } };
        AddRow(grid, name, cb);
    }

    private void AddRow(TableLayoutPanel grid, Control name, Control value)
    {
        int r = grid.RowCount;
        grid.RowCount = r + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(name, 0, r);
        grid.Controls.Add(value, 1, r);
    }
}
