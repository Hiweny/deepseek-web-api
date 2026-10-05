using Microsoft.Win32;

namespace DSWebApi.Desktop.Core;

/// <summary>开机自启（当前用户 Run 键，无需管理员）。</summary>
public static class AutoRun
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeepSeekWebAPI";

    public static void Set(bool enabled, string exePath)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath)) exePath = Environment.ProcessPath;
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (enabled) k.SetValue(ValueName, "\"" + exePath + "\" --min");
            else k.DeleteValue(ValueName, false);
            Log.Write("开机自启已" + (enabled ? "开启" : "关闭"));
        }
        catch (Exception e)
        {
            Log.Write("设置开机自启失败: " + e.Message);
        }
    }

    public static bool Get()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return k?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }
}
