using System.Reflection;
using DSWebApi.Desktop.Core;
using DSWebApi.Desktop.UI;

namespace DSWebApi.Desktop;

internal static class Program
{
    public const string AppName = "DeepSeek Web API";
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.5";

    private static Mutex _singleInstance;

    [STAThread]
    private static void Main(string[] args)
    {
        Log.Init();
        Log.Write("===== 启动 " + AppName + " v" + Version + " =====");
        Log.Write("命令行: " + string.Join(" ", args));
        Log.Write("OS: " + Environment.OSVersion + " 64bit=" + Environment.Is64BitOperatingSystem);
        Log.Write("目录: " + AppDomain.CurrentDomain.BaseDirectory);
        Log.Write("日志: " + Log.FilePath);

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }
        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(AppName + " " + Version);
            return;
        }

        // 全局异常兜底：绝不让程序静默退出
        Application.ThreadException += (s, e) =>
        {
            Log.Write("!! UI 线程异常: " + e.Exception);
            try
            {
                MessageBox.Show("发生异常（已写入日志）：\n" + e.Exception.Message + "\n\n日志：" + Log.FilePath,
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log.Write("!! 未处理异常: " + e.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log.Write("!! 未观察的任务异常: " + e.Exception.Message);
            e.SetObserved();
        };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool created;
        _singleInstance = new Mutex(true, "Local\\DeepSeekWebAPI_Desktop_Single", out created);
        if (!created)
        {
            Log.Write("检测到已有实例，激活其窗口后退出");
            try { EventWaitHandle.OpenExisting("Local\\DeepSeekWebAPI_Show").Set(); } catch { }
            return;
        }

        Prefs.Load();
        WebBridge.I.BridgeJs = LoadEmbedded("Assets.bridge.js");
        Log.Write("bridge.js 已载入: " + WebBridge.I.BridgeJs.Length + " 字节");

        // 先起 HTTP 服务：即使窗口/WebView2 出问题，接口也能提供服务（日志可查）
        try { ChatEngine.I.StartAll(Prefs.Port); }
        catch (Exception e) { Log.Write("!! 启动 HTTP 服务失败: " + e.Message); }

        bool startMinimized = args.Any(a => a.Equals("--min", StringComparison.OrdinalIgnoreCase));

        try
        {
            Application.Run(new MainForm(startMinimized));
        }
        catch (Exception e)
        {
            Log.Write("!! 主窗口异常退出: " + e);
            try { MessageBox.Show("程序异常退出：" + e.Message + "\n\n日志：" + Log.FilePath, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
        }
        finally
        {
            Log.Write("===== 退出 =====");
            try { _singleInstance.ReleaseMutex(); } catch { }
        }
    }

    public static string LoadEmbedded(string name)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream(asm.GetName().Name + "." + name);
            if (s == null)
            {
                Log.Write("!! 找不到内嵌资源: " + name);
                return "";
            }
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch (Exception e)
        {
            Log.Write("!! 读取内嵌资源失败 " + name + ": " + e.Message);
            return "";
        }
    }
}
