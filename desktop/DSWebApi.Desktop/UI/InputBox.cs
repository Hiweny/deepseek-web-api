namespace DSWebApi.Desktop.UI;

/// <summary>简洁的文本输入对话框。</summary>
internal static class InputBox
{
    public static string Show(IWin32Window owner, string title, string initial)
    {
        using var f = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(420, 130),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            BackColor = Color.FromArgb(0x18, 0x1D, 0x26),
            ForeColor = Color.FromArgb(0xE7, 0xEA, 0xF0),
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        var tb = new TextBox
        {
            Text = initial ?? "",
            Left = 20, Top = 24, Width = 380,
            BackColor = Color.FromArgb(0x0F, 0x13, 0x19),
            ForeColor = Color.FromArgb(0xE7, 0xEA, 0xF0),
            BorderStyle = BorderStyle.FixedSingle,
        };
        var ok = new Button
        {
            Text = "确定", DialogResult = DialogResult.OK,
            Left = 240, Top = 74, Width = 76, Height = 30, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0x1C, 0x27, 0x3D), ForeColor = Color.FromArgb(0x5A, 0x94, 0xF8),
        };
        ok.FlatAppearance.BorderColor = Color.FromArgb(0x27, 0x2E, 0x3A);
        var cancel = new Button
        {
            Text = "取消", DialogResult = DialogResult.Cancel,
            Left = 324, Top = 74, Width = 76, Height = 30, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0x18, 0x1D, 0x26), ForeColor = Color.FromArgb(0x98, 0xA2, 0xB2),
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(0x27, 0x2E, 0x3A);
        f.Controls.Add(tb);
        f.Controls.Add(ok);
        f.Controls.Add(cancel);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        tb.SelectAll();
        return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
    }
}
