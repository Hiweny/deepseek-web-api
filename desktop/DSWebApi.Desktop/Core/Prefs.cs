namespace DSWebApi.Desktop.Core;

/// <summary>设置持久化（%LOCALAPPDATA%\DeepSeekWebAPI\settings.json）。默认值与 APK 一致。</summary>
public static class Prefs
{
    private static readonly object _lk = new object();
    private static JObj _data = new JObj();

    private static string FilePath =>
        Path.Combine(Log.Dir ?? Path.GetTempPath(), "settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var t = File.ReadAllText(FilePath);
                var v = Json.TryParse(t);
                if (v is JObj o) { lock (_lk) _data = o; }
            }
        }
        catch (Exception e) { Log.Write("读取设置失败(用默认值): " + e.Message); }
    }

    public static void Save()
    {
        try
        {
            string json;
            lock (_lk) json = _data.ToJson();
            File.WriteAllText(FilePath, json);
        }
        catch (Exception e) { Log.Write("保存设置失败: " + e.Message); }
    }

    /* ---------------- 读取 ---------------- */

    public static int GetInt(string key, int def)
    {
        lock (_lk) return _data.Int(key, def);
    }

    public static string GetStr(string key, string def)
    {
        lock (_lk)
        {
            if (!_data.Has(key)) return def;
            return _data.Str(key, def);
        }
    }

    public static bool GetBool(string key, bool def)
    {
        lock (_lk)
        {
            if (!_data.Has(key)) return def;
            return _data.Bool(key, def);
        }
    }

    /* ---------------- 写入 ---------------- */

    public static void Set(string key, int v) { lock (_lk) _data.Set(key, (long)v); SaveSafe(); }
    public static void Set(string key, string v) { lock (_lk) _data.Set(key, v ?? ""); SaveSafe(); }
    public static void Set(string key, bool v) { lock (_lk) _data.Set(key, v); SaveSafe(); }
    public static void Remove(string key) { lock (_lk) _data.Remove(key); SaveSafe(); }

    private static void SaveSafe()
    {
        try { Save(); } catch { }
    }

    /* ---------------- 语义化访问（对齐 APK 的键与默认值） ---------------- */

    public static int Port { get => GetInt("port", 8787); set => Set("port", value); }
    public static int ActivePort { get => GetInt("port_active", 0); set => Set("port_active", value); }
    public static string ApiKey { get => GetStr("api_key", "sk-deepseek"); set => Set("api_key", value); }
    public static int TimeoutSec { get => GetInt("timeout", 300); set => Set("timeout", value); }
    public static string ThinkingMode { get => GetStr("thinking_mode", "auto"); set => Set("thinking_mode", value); }
    public static string SearchMode { get => GetStr("search_mode", "auto"); set => Set("search_mode", value); }
    public static bool Stateless { get => GetBool("stateless", true); set => Set("stateless", value); }
    public static bool AutoNewChat { get => GetBool("auto_newchat", true); set => Set("auto_newchat", value); }
    public static int ContextTokens { get => GetInt("context_tokens", 1000000); set => Set("context_tokens", value); }
    public static int NewChatThreshold { get => GetInt("newchat_threshold", 70); set => Set("newchat_threshold", value); }

    // 桌面端特有
    public static bool CloseToTray { get => GetBool("close_to_tray", true); set => Set("close_to_tray", value); }
    public static bool AutoStart { get => GetBool("autostart", false); set => Set("autostart", value); }
    public static bool LanEnabled { get => GetBool("lan_enabled", true); set => Set("lan_enabled", value); }
    public static bool StartOnBootServer { get => GetBool("server_on_boot", true); set => Set("server_on_boot", value); }
}
