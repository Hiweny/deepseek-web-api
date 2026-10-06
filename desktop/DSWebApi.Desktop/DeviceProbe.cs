using System.Text;
using DSWebApi.Desktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DSWebApi.Desktop;

/// <summary>
/// 设备指纹隔离探针（--probe-device [数量] [--profiles]）：
/// 并排创建 N 个 WebView2，各自读取 chat.deepseek.com 的数美设备指纹（device_id），
/// 用于验证「每账号独立数据目录 / 独立 profile」是否真的能得到互不相同的设备指纹。
///
/// 两种模式：
///   --probe-device 3              每个实例用**独立 UserDataFolder**（独立浏览器进程，隔离最彻底）
///   --probe-device 3 --profiles   共用同一个 UserDataFolder，但各自**独立 profile**（省内存）
/// device_id 只打印前 16 位 + 长度，绝不打印完整值。
/// </summary>
internal static class DeviceProbe
{
    private const string SignInUrl = "https://chat.deepseek.com/sign_in";
    private static int _exit = 1;

    public static int Run(int count, bool useProfiles)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ProbeWindow(count, useProfiles));
        return _exit;
    }

    private sealed class ProbeWindow : Form
    {
        private readonly int _count;
        private readonly bool _profiles;
        private readonly List<WebView2> _views = new List<WebView2>();
        private readonly List<int> _navDone = new List<int>();
        private readonly string _runId = DateTime.Now.ToString("MMdd-HHmmss");

        public ProbeWindow(int count, bool useProfiles)
        {
            _count = Math.Max(1, Math.Min(8, count));
            _profiles = useProfiles;
            Text = "DeepSeek Web API · 设备指纹隔离探针";
            Size = new Size(1500, 900);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(0x0D, 0x10, 0x15);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = _count, RowCount = 1, BackColor = Color.Black,
            };
            for (int i = 0; i < _count; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / _count));
            Controls.Add(grid);

            for (int i = 0; i < _count; i++)
            {
                var wv = new WebView2 { Dock = DockStyle.Fill };
                grid.Controls.Add(wv, i, 0);
                _views.Add(wv);
            }
        }

        private static void P(string msg) => Log.Write("[PROBE] " + msg);

        private static string Short(string v) =>
            string.IsNullOrEmpty(v) ? "(空)" : (v.Length <= 16 ? v : v.Substring(0, 16) + "…") + " [len=" + v.Length + "]";

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _ = Flow();
        }

        private async Task Flow()
        {
            int code = 1;
            try { code = await FlowCore(); }
            catch (Exception ex) { P("异常: " + ex); }
            P("===== 探针结束 exit=" + code + " =====");
            _exit = code;
            try { Close(); } catch { }
        }

        private async Task<int> FlowCore()
        {
            string root = Path.Combine(Log.Dir, "probe", _runId);
            P("===== 设备指纹探针开始 · 实例数=" + _count + " · 模式=" + (_profiles ? "同一 UDF + 多 profile" : "每实例独立 UDF") + " =====");
            P("数据根目录: " + root);

            CoreWebView2Environment sharedEnv = null;
            string sharedUdf = Path.Combine(root, "shared");
            if (_profiles)
            {
                Directory.CreateDirectory(sharedUdf);
                sharedEnv = await CoreWebView2Environment.CreateAsync(null, sharedUdf);
                P("共享 UDF: " + sharedUdf);
            }

            var tasks = new List<Task>();
            for (int i = 0; i < _count; i++)
            {
                int idx = i;
                tasks.Add(InitOneAsync(idx, root, sharedEnv));
            }
            await Task.WhenAll(tasks);

            // 等所有页面加载完
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (_navDone.Count < _count && DateTime.UtcNow < deadline) await Task.Delay(500);
            P("页面加载完成 " + _navDone.Count + "/" + _count);

            // 给数美 SDK 留初始化时间，然后再多采两轮（有些指纹是延迟生成的）
            var ids = new string[_count];
            var details = new string[_count];
            for (int round = 0; round < 3; round++)
            {
                await Task.Delay(round == 0 ? 5000 : 4000);
                for (int i = 0; i < _count; i++)
                {
                    var r = await ProbeOneAsync(i);
                    ids[i] = r.id;
                    details[i] = r.detail;
                    P("第 " + (round + 1) + " 轮 · 实例#" + i + " device_id=" + Short(r.id) + " | " + r.detail);
                }
                if (ids.All(x => !string.IsNullOrEmpty(x))) break;
            }

            P("---- 汇总 ----");
            for (int i = 0; i < _count; i++) P("实例#" + i + " -> " + Short(ids[i]));
            int distinct = ids.Where(x => !string.IsNullOrEmpty(x)).Distinct().Count();
            int got = ids.Count(x => !string.IsNullOrEmpty(x));
            P("取到指纹 " + got + "/" + _count + "，去重后 " + distinct + " 个 → " +
              (got == 0 ? "❌ 未取到 device_id（需改用网络 hook 方式）"
               : distinct == got && got == _count ? "✅ 每个实例指纹都不同（隔离成功）"
               : "⚠️ 存在重复指纹（隔离不足）"));
            P("明细: " + string.Join(" || ", details));

            if (got == 0) return 2;
            return distinct == _count ? 0 : 3;
        }

        private async Task InitOneAsync(int idx, string root, CoreWebView2Environment sharedEnv)
        {
            try
            {
                var wv = _views[idx];
                CoreWebView2Environment env;
                CoreWebView2ControllerOptions opts = null;
                if (_profiles)
                {
                    env = sharedEnv;
                    opts = env.CreateCoreWebView2ControllerOptions();
                    opts.ProfileName = "acct" + idx;
                }
                else
                {
                    string udf = Path.Combine(root, "u" + idx);
                    Directory.CreateDirectory(udf);
                    env = await CoreWebView2Environment.CreateAsync(null, udf);
                }

                await wv.EnsureCoreWebView2Async(env, opts);
                var core = wv.CoreWebView2;
                try { core.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"; } catch { }
                core.Settings.IsStatusBarEnabled = false;
                core.NavigationCompleted += (s, e) =>
                {
                    lock (_navDone) { if (!_navDone.Contains(idx)) _navDone.Add(idx); }
                    P("实例#" + idx + " 加载" + (e.IsSuccess ? "成功" : "失败") + " profile=" + (_profiles ? "acct" + idx : "独立UDF"));
                };
                core.Navigate(SignInUrl);
            }
            catch (Exception ex)
            {
                P("实例#" + idx + " 初始化失败: " + ex.Message);
            }
        }

        private async Task<(string id, string detail)> ProbeOneAsync(int idx)
        {
            try
            {
                var core = _views[idx].CoreWebView2;
                if (core == null) return ("", "core=null");
                string raw = await core.ExecuteScriptAsync(ProbeJs);
                // ExecuteScriptAsync 返回的是 JSON 编码后的字符串
                string json = raw;
                try
                {
                    var v = Json.TryParse(raw);
                    if (v is JStr js) json = js.V;
                }
                catch { }

                var o = Json.TryParse(json) as JObj;
                if (o == null) return ("", "解析失败: " + (json.Length > 120 ? json.Substring(0, 120) : json));

                string id = o.Str("deviceId", "");
                string detail = "smsdk=" + o.Str("smsdk", "?")
                    + " globals=[" + JoinArr(o.Arr("globals"), 12) + "]"
                    + " lsKeys=[" + JoinArr(o.Arr("lsKeys"), 8) + "]"
                    + " cmKeys=[" + JoinArr(o.Arr("cookieKeys"), 8) + "]";
                return (id, detail);
            }
            catch (Exception ex)
            {
                return ("", "读取异常: " + ex.Message);
            }
        }


        private static string JoinArr(JArr a, int max)
        {
            if (a == null) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < a.Count && i < max; i++)
            {
                if (i > 0) sb.Append(',');
                var v = a[i];
                sb.Append(v == null ? "" : v.ToString());
            }
            return sb.ToString();
        }

        /// <summary>在页面里读取数美 device_id（只返回键名与前 16 位，不返回完整指纹值）。</summary>
        private const string ProbeJs = @"
(function () {
  var out = { url: location.href, title: document.title, deviceId: '', smsdk: typeof window.SMSdk, globals: [], lsKeys: [], cookieKeys: [] };
  function pick(v) {
    if (!v) return '';
    v = String(v);
    if (v.length >= 40 && /^[A-Za-z0-9+/=_\-]{40,}$/.test(v)) return v;
    return '';
  }
  try {
    var g = [];
    for (var k in window) { if (/^(SMSdk|SMSdkInstance|_sm|smDeviceId|smsdk)$/i.test(k) || /deviceid|shumei/i.test(k)) g.push(k); }
    out.globals = g.slice(0, 12);
  } catch (e) {}
  try { if (window.SMSdk && typeof window.SMSdk.getDeviceId === 'function') out.deviceId = String(window.SMSdk.getDeviceId() || ''); } catch (e) { out.deviceId = ''; }
  try {
    if (!out.deviceId && window.SMSdk && typeof window.SMSdk.getDeviceIdSync === 'function') out.deviceId = String(window.SMSdk.getDeviceIdSync() || '');
  } catch (e) {}
  try {
    for (var i = 0; i < localStorage.length && i < 80; i++) {
      var k = localStorage.key(i);
      out.lsKeys.push(k);
      if (!out.deviceId && /device|smid|shumei|smsdk|risk|fingerprint/i.test(k)) { var v = pick(localStorage.getItem(k)); if (v) out.deviceId = v; }
    }
  } catch (e) {}
  try {
    var cs = (document.cookie || '').split(';');
    for (var j = 0; j < cs.length; j++) {
      var kv = cs[j].split('=');
      var ck = (kv[0] || '').trim();
      if (!ck) continue;
      out.cookieKeys.push(ck);
      if (!out.deviceId && /device|smid|shumei|smsdk|risk/i.test(ck)) { var cv = pick((kv[1] || '').trim()); if (cv) out.deviceId = cv; }
    }
  } catch (e) {}
  return JSON.stringify(out);
})();
";
    }
}
