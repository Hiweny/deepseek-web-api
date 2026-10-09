using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace DSWebApi.Desktop.Core;

/// <summary>
/// Cloudflare Tunnel 内网穿透：把本机 http://127.0.0.1:{port} 暴露到固定公网域名。
///
/// 全流程自动化（用户只需提供 CF API Token + 公网域名）：
///   1) 用 Token 发现 account / zone
///   2) 按名字（deepseek-web-api）查找或创建命名隧道
///   3) 用 API 写隧道 ingress（hostname → http://127.0.0.1:port）
///   4) 用 API 绑定 DNS（CNAME hostname → {tid}.cfargotunnel.com，proxied）
///   5) 取隧道 token，拉起 cloudflared（token 模式，无需 cert.pem / 浏览器登录）
///   ⚠️ 换域名时只要改 TunnelHostname，下次启动会自动重新绑定。
///
/// Cloudflare Tunnel 本身**免费**（Zero Trust 免费版即可，隧道数量不限）。
/// </summary>
public sealed class CloudflareTunnel
{
    public static readonly CloudflareTunnel I = new CloudflareTunnel();

    private const string Api = "https://api.cloudflare.com/client/v4";
    private const string TunnelName = "deepseek-web-api";
    private const string CloudflaredUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

    private Process _proc;
    private volatile string _state = "未启用";
    private volatile string _publicUrl = "";
    private volatile string _tunnelId = "";
    private volatile string _lastLog = "";
    private volatile bool _busy;

    public string State => _state;
    public string PublicUrl => _publicUrl;
    public string TunnelId => _tunnelId;
    public string LastLog => _lastLog;
    public bool Busy => _busy;
    public bool Running { get { var p = _proc; return p != null && !p.HasExited; } }

    /// <summary>状态变化回调（UI 刷新）。可能在后台线程触发。</summary>
    public event Action Changed;

    private void Set(string s) { _state = s; Log.Write("[隧道] " + s); Notify(); }
    private void Notify() { try { Changed?.Invoke(); } catch { } }

    /* ================= 对外 ================= */

    /// <summary>后台线程执行完整启动流程（不阻塞 UI）。</summary>
    public void StartAsync() { if (_busy) return; Task.Run(() => { try { StartCore(); } catch (Exception e) { Set("失败：" + e.Message); } }); }

    /// <summary>停止 cloudflared（不影响云端隧道与 DNS 记录）。</summary>
    public void Stop()
    {
        try { if (_proc != null && !_proc.HasExited) { _proc.Kill(true); } } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        _publicUrl = "";
        if (_state.StartsWith("已连接") || _state.StartsWith("启动中")) Set("已停止");
        Notify();
    }

    /* ================= 主流程 ================= */

    private void StartCore()
    {
        _busy = true;
        Notify();
        try
        {
            StopQuiet();

            string token = Prefs.TunnelApiToken.Trim();
            string host = Prefs.TunnelHostname.Trim().TrimStart('.');
            if (token.Length == 0) { Set("失败：未填写 Cloudflare API Token"); return; }
            if (host.Length == 0) { Set("失败：未填写公网域名"); return; }

            int port = Prefs.Port;
            Set("查询账户…");
            string accountId = FindAccountId(token);
            Set("查询域名…");
            string zoneId = FindZoneId(token, host);

            Set("准备隧道…");
            string tid = EnsureTunnel(token, accountId);
            _tunnelId = tid; Prefs.TunnelId = tid;

            Set("配置入口…");
            SetIngress(token, accountId, tid, host, "http://127.0.0.1:" + port);

            Set("绑定域名…");
            EnsureDns(token, zoneId, host, tid);

            Set("获取隧道凭据…");
            string ttoken = GetTunnelToken(token, accountId, tid);

            Set("准备 cloudflared…");
            string exe = ResolveCloudflared();

            Set("启动隧道…");
            Launch(exe, ttoken);

            _publicUrl = "https://" + host;
            Set("已连接");
        }
        finally
        {
            _busy = false;
            Notify();
        }
    }

    private void StopQuiet()
    {
        try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }

    /* ================= Cloudflare API ================= */

    private static JVal Call(string token, string method, string path, JObj body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), Api + path);
        req.Headers.Add("Authorization", "Bearer " + token);
        if (body != null) req.Content = new StringContent(body.ToJson(), Encoding.UTF8, "application/json");
        var resp = Http.SendAsync(req).GetAwaiter().GetResult();
        string txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var j = Json.TryParse(txt) as JObj;
        if (j == null) throw new Exception("API 返回无法解析（HTTP " + (int)resp.StatusCode + "）");
        if (!j.Bool("success", false))
            throw new Exception(((int)resp.StatusCode) + " " + (j.Get("errors") != null ? j.Get("errors").ToJson() : ""));
        return j.Get("result");
    }

    private static string FindAccountId(string token)
    {
        var arr = Call(token, "GET", "/accounts?per_page=50", null) as JArr;
        if (arr == null || arr.Count == 0) throw new Exception("该 Token 看不到任何账户");
        var first = arr[0] as JObj;
        string id = first == null ? "" : first.Str("id", "");
        if (id.Length == 0) throw new Exception("账户 id 解析失败");
        return id;
    }

    private static string FindZoneId(string token, string host)
    {
        var arr = Call(token, "GET", "/zones?per_page=100", null) as JArr;
        if (arr == null || arr.Count == 0) throw new Exception("该 Token 看不到任何域名（zone）");
        JObj best = null; int bestLen = -1;
        for (int i = 0; i < arr.Count; i++)
        {
            var z = arr[i] as JObj; if (z == null) continue;
            string zn = z.Str("name", "").Trim();
            if (zn.Length == 0) continue;
            if (host.Equals(zn, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + zn, StringComparison.OrdinalIgnoreCase))
            {
                if (zn.Length > bestLen) { best = z; bestLen = zn.Length; }   // 取最长的后缀匹配
            }
        }
        if (best == null) throw new Exception("域名 " + host + " 不在该账户的托管域名内");
        return best.Str("id", "");
    }

    private static string EnsureTunnel(string token, string accountId)
    {
        var arr = Call(token, "GET", "/accounts/" + accountId + "/cfd_tunnel?is_deleted=false&per_page=100", null) as JArr;
        if (arr != null)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var t = arr[i] as JObj; if (t == null) continue;
                if (string.Equals(t.Str("name", ""), TunnelName, StringComparison.OrdinalIgnoreCase))
                    return t.Str("id", "");
            }
        }
        var body = new JObj();
        body.Set("name", TunnelName);
        body.Set("config_src", "cloudflare");
        var r = Call(token, "POST", "/accounts/" + accountId + "/cfd_tunnel", body) as JObj;
        if (r == null) throw new Exception("创建隧道失败");
        return r.Str("id", "");
    }

    private static void SetIngress(string token, string accountId, string tid, string host, string service)
    {
        var body = new JObj();
        var cfg = new JObj();
        var ingress = new JArr();
        var e1 = new JObj();
        e1.Set("hostname", host);
        e1.Set("service", service);
        e1.Set("originRequest", OriginNoTlsVerify());
        var e2 = new JObj();
        e2.Set("service", "http_status:404");
        ingress.Add(e1); ingress.Add(e2);
        cfg.Set("ingress", ingress);
        body.Set("config", cfg);
        Call(token, "PUT", "/accounts/" + accountId + "/cfd_tunnel/" + tid + "/configurations", body);
    }

    /// <summary>本机回环地址无需校验证书（部分部署会报证书错误）。</summary>
    private static JObj OriginNoTlsVerify()
    {
        var o = new JObj();
        o.Set("noTLSVerify", true);
        o.Set("httpHostHeader", "127.0.0.1");
        return o;
    }

    private static void EnsureDns(string token, string zoneId, string host, string tid)
    {
        string target = tid + ".cfargotunnel.com";
        var arr = Call(token, "GET", "/zones/" + zoneId + "/dns_records?per_page=100&name=" + Uri.EscapeDataString(host), null) as JArr;
        if (arr != null && arr.Count > 0)
        {
            var rec = arr[0] as JObj;
            string rid = rec == null ? "" : rec.Str("id", "");
            bool same = rec != null
                && string.Equals(rec.Str("type", ""), "CNAME", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rec.Str("content", ""), target, StringComparison.OrdinalIgnoreCase)
                && rec.Bool("proxied", true);
            if (same) return;   // 已正确绑定
            var upd = new JObj();
            upd.Set("type", "CNAME"); upd.Set("name", host); upd.Set("content", target); upd.Set("proxied", true);
            Call(token, "PUT", "/zones/" + zoneId + "/dns_records/" + rid, upd);
            return;
        }
        var body = new JObj();
        body.Set("type", "CNAME"); body.Set("name", host); body.Set("content", target);
        body.Set("proxied", true); body.Set("ttl", (long)1);
        Call(token, "POST", "/zones/" + zoneId + "/dns_records", body);
    }

    private static string GetTunnelToken(string token, string accountId, string tid)
    {
        var r = Call(token, "GET", "/accounts/" + accountId + "/cfd_tunnel/" + tid + "/token", null);
        if (r is JStr js) return js.V.Trim();
        if (r is JObj jo) return jo.Str("token", "").Trim();
        throw new Exception("取隧道 token 失败");
    }

    /* ================= cloudflared ================= */

    private string ResolveCloudflared()
    {
        string p = Prefs.CloudflaredPath.Trim().Trim('"');
        if (p.Length > 0)
        {
            if (File.Exists(p)) return p;
            throw new Exception("找不到 cloudflared.exe：" + p);
        }
        string def = Path.Combine(Log.Dir ?? Path.GetTempPath(), "cloudflared.exe");
        if (File.Exists(def) && new FileInfo(def).Length > 1_000_000) return def;
        Set("下载 cloudflared（约 60MB，仅首次）…");
        try
        {
            using var resp = Http.GetAsync(CloudflaredUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            using (var fs = File.Create(def))
                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            try { File.Delete(def); } catch { }
            throw new Exception("下载 cloudflared 失败（" + e.Message + "）。可手动下载后填到「cloudflared 路径」：\n" + CloudflaredUrl);
        }
        return def;
    }

    private void Launch(string exe, string tunnelToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "tunnel --no-autoupdate --loglevel info run --token " + tunnelToken,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) { _lastLog = e.Data; Log.Write("[cloudflared] " + e.Data); } };
        p.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) { _lastLog = e.Data; } };
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        _proc = p;
    }
}
