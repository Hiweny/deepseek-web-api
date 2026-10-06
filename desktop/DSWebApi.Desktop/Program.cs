using System.Reflection;
using System.Text;
using DSWebApi.Desktop.Core;
using DSWebApi.Desktop.UI;

namespace DSWebApi.Desktop;

internal static class Program
{
    public const string AppName = "DeepSeek Web API";
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.6";

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
        int probeAt = Array.FindIndex(args, a => a.Equals("--probe-device", StringComparison.OrdinalIgnoreCase));
        if (probeAt >= 0)
        {
            int probeCount = 2;
            if (probeAt + 1 < args.Length && int.TryParse(args[probeAt + 1], out var pc)) probeCount = pc;
            bool useProfiles = args.Any(a => a.Equals("--profiles", StringComparison.OrdinalIgnoreCase));
            Environment.ExitCode = DeviceProbe.Run(probeCount, useProfiles);
            return;
        }

        int e2eAt = Array.FindIndex(args, a => a.Equals("--e2e", StringComparison.OrdinalIgnoreCase));
        if (e2eAt >= 0)
        {
            // 优先从命令行取；CI 用环境变量传入（避免密码出现在进程命令行里）
            string phone = e2eAt + 1 < args.Length ? args[e2eAt + 1] : Environment.GetEnvironmentVariable("DS_E2E_PHONE");
            string pwd = e2eAt + 2 < args.Length ? args[e2eAt + 2] : Environment.GetEnvironmentVariable("DS_E2E_PASSWORD");
            Environment.ExitCode = E2ETest.Run(phone, pwd);
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
        WebBridge.I.BridgeJs = LoadEmbedded("bridge.js");
        Log.Write("bridge.js 已载入: " + WebBridge.I.BridgeJs.Length + " 字节");
        if (WebBridge.I.BridgeJs.Length < 200)
            Log.Write("!!!!!! 严重: 桥接脚本 bridge.js 未载入，接口调用将全部失败（NO_BRIDGE）。请把 bridge.js 放到 exe 同目录的 Assets\\ 下后重启。");

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

    /// <summary>
    /// 读取内嵌资源。默认逻辑名 = RootNamespace + 目录 + 文件名（本项目为 DSWebApi.Desktop.Assets.bridge.js），
    /// 因此绝不能用 AssemblyName 拼接；这里按「精确名 → 后缀匹配」查找，并带磁盘兜底。
    /// </summary>
    public static string LoadEmbedded(string simpleName)
    {
        var asm = Assembly.GetExecutingAssembly();
        try
        {
            var names = asm.GetManifestResourceNames();
            string hit = names.FirstOrDefault(n => string.Equals(n, simpleName, StringComparison.OrdinalIgnoreCase))
                      ?? names.FirstOrDefault(n => n.EndsWith("." + simpleName, StringComparison.OrdinalIgnoreCase))
                      ?? names.FirstOrDefault(n => n.EndsWith("Assets." + simpleName, StringComparison.OrdinalIgnoreCase))
                      ?? names.FirstOrDefault(n => n.EndsWith(simpleName, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                using var s = asm.GetManifestResourceStream(hit);
                if (s != null)
                {
                    using var r = new StreamReader(s, new UTF8Encoding(false), true);
                    string text = r.ReadToEnd();
                    if (text.Length > 0)
                    {
                        Log.Write("内嵌资源 " + simpleName + " ← " + hit + "（" + text.Length + " 字节）");
                        return text;
                    }
                    Log.Write("!! 内嵌资源为空: " + hit);
                }
            }
            Log.Write("!! 未找到内嵌资源 " + simpleName + "；程序集内可用资源: " + string.Join(", ", names));
        }
        catch (Exception e)
        {
            Log.Write("!! 读取内嵌资源失败 " + simpleName + ": " + e.Message);
        }

        // 兜底：exe 同目录（用户可自行放一份 Assets\bridge.js 修复）
        foreach (var path in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", simpleName),
            Path.Combine(AppContext.BaseDirectory, simpleName),
        })
        {
            try
            {
                if (File.Exists(path))
                {
                    string t = File.ReadAllText(path, new UTF8Encoding(false));
                    if (t.Length > 0) { Log.Write("从磁盘载入 " + simpleName + ": " + path + "（" + t.Length + " 字节）"); return t; }
                }
            }
            catch { }
        }
        return "";
    }
}
