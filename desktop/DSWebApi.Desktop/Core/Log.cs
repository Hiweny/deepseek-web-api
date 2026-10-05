using System.Text;

namespace DSWebApi.Desktop.Core;

/// <summary>内存 + 文件双写日志，便于「打不开」时远程定位。</summary>
public static class Log
{
    private const int MAX = 1000;
    private static readonly object _lk = new object();
    private static readonly List<string> _buf = new List<string>();
    private static string _file;

    /// <summary>%LOCALAPPDATA%\DeepSeekWebAPI</summary>
    public static string Dir { get; private set; }

    public static string FilePath => _file;

    /// <summary>新增一行时触发（UI 订阅）。</summary>
    public static event Action<string> Line;

    public static void Init()
    {
        try
        {
            Dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekWebAPI");
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(Path.Combine(Dir, "WebView2"));
            _file = Path.Combine(Dir, "app.log");
            // 简单轮转：超过 4MB 归档
            try
            {
                var fi = new FileInfo(_file);
                if (fi.Exists && fi.Length > 4 * 1024 * 1024)
                {
                    var bak = Path.Combine(Dir, "app.old.log");
                    try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                    File.Move(_file, bak);
                }
            }
            catch { }
        }
        catch
        {
            Dir = Path.GetTempPath();
            _file = Path.Combine(Dir, "dswebapi-app.log");
        }
    }

    public static void Write(string msg)
    {
        string line = "[" + DateTime.Now.ToString("MM-dd HH:mm:ss") + "] " + msg;
        lock (_lk)
        {
            _buf.Add(line);
            if (_buf.Count > MAX) _buf.RemoveAt(0);
            try
            {
                if (_file != null) File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
        try { Line?.Invoke(line); } catch { }
        try { Console.WriteLine(line); } catch { }
    }

    public static string Text()
    {
        lock (_lk)
        {
            int from = Math.Max(0, _buf.Count - 400);
            var sb = new StringBuilder();
            for (int i = from; i < _buf.Count; i++) sb.Append(_buf[i]).Append('\n');
            return sb.ToString();
        }
    }

    public static void Clear()
    {
        lock (_lk) _buf.Clear();
    }

    public static void Inbox(string title, string body)
    {
        Write("===== " + title + " =====");
        foreach (var l in (body ?? "").Split('\n')) Write("  " + l);
        Write("===== /" + title + " =====");
    }
}
