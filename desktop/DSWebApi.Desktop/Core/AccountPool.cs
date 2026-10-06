namespace DSWebApi.Desktop.Core;

/// <summary>
/// 一个账号槽 = 一个独立 WebView2 数据目录（独立 cookie / localStorage / 设备指纹）
///                + 一个 WebBridge 实例 + 一个 WebView2 控件（由 UI 层持有）。
/// </summary>
public sealed class AccountSlot
{
    public string Id = "";
    public string Name = "";
    public bool Enabled = true;
    /// <summary>内置主账号：复用原有数据目录与登录态（开关关闭时用的就是它）。</summary>
    public bool IsPrimary;

    /* ---- 运行态 ---- */
    public WebBridge Bridge;
    public object View;                 // Microsoft.Web.WebView2.WinForms.WebView2（避免 Core 依赖 UI 类型）
    public bool PageReady;
    public bool LoggedIn;
    public bool Probed;
    public int SentInSession;           // 本轮会话已发送次数（达到上限就换号）
    public string SessionId = "";
    public long CooldownUntil;          // unix ms；>now 表示冷却中
    public long LastUsedMs;
    public int TotalCalls;
    public int OkCalls;
    public int RateLimitedCount;
    public string LastError = "";
    public string Note = "未初始化";

    public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool InCooldown => CooldownUntil > NowMs;
    public int CooldownLeftSec => InCooldown ? (int)Math.Ceiling((CooldownUntil - NowMs) / 1000.0) : 0;

    public string DataDir => Path.Combine(Log.Dir ?? Path.GetTempPath(), "accounts", Id);

    public string StateText
    {
        get
        {
            if (!Enabled) return "已停用";
            if (InCooldown) return "冷却 " + FmtLeft(CooldownLeftSec);
            if (LoggedIn) return "就绪 · 本轮已发 " + SentInSession + "/" + Prefs.SessionSendLimit;
            if (!Probed) return "加载中…";
            return "未登录";
        }
    }

    public static string FmtLeft(int sec)
    {
        if (sec <= 0) return "—";
        if (sec < 60) return sec + "s";
        if (sec < 3600) return (sec / 60) + "m" + (sec % 60 > 0 ? (sec % 60) + "s" : "");
        return (sec / 3600) + "h" + ((sec % 3600) / 60) + "m";
    }

    public JObj ToJson()
    {
        var o = new JObj();
        o.Set("id", Id);
        o.Set("name", Name);
        o.Set("enabled", Enabled);
        o.Set("primary", IsPrimary);
        return o;
    }

    public static AccountSlot FromJson(JObj o)
    {
        var s = new AccountSlot();
        s.Id = o.Str("id", "");
        s.Name = o.Str("name", "");
        s.Enabled = o.Bool("enabled", true);
        s.IsPrimary = o.Bool("primary", false);
        if (s.Id.Length == 0) s.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        return s;
    }
}

/// <summary>
/// 账号池 + 轮换调度。
///
/// 轮换策略（按用户要求，不是"每小时配额"，而是"给每个账号的会话留缓冲"）：
///   1. 一个账号在**同一会话里最多连发 N 次**（默认 5），到点就换下一个账号；
///   2. 谁最久没用过谁先上（保证轮流，不会总用同一个号）；
///   3. 某账号出现「消息发送频繁」→ 立刻进入冷却（默认 30 分钟），期间不参与调度；
///   4. 所有账号都在冷却 → 直接返回 429 + Retry-After，而不是硬打上游（避免把号打废）。
/// </summary>
public sealed class AccountPool
{
    public static readonly AccountPool I = new AccountPool();

    private readonly object _lk = new object();
    private readonly List<AccountSlot> _slots = new List<AccountSlot>();

    public event Action Changed;

    public void Notify() { try { Changed?.Invoke(); } catch { } }

    public List<AccountSlot> Snapshot() { lock (_lk) return new List<AccountSlot>(_slots); }

    public AccountSlot Find(string id)
    {
        lock (_lk) return _slots.FirstOrDefault(s => s.Id == id);
    }

    /* ================= 持久化 ================= */

    public void LoadFromPrefs()
    {
        var list = new List<AccountSlot>();
        try
        {
            string raw = Prefs.AccountsJson;
            if (!string.IsNullOrEmpty(raw))
            {
                var arr = Json.TryParse(raw) as JArr;
                if (arr != null)
                {
                    for (int i = 0; i < arr.Count; i++)
                    {
                        var o = arr[i] as JObj;
                        if (o != null) list.Add(AccountSlot.FromJson(o));
                    }
                }
            }
        }
        catch (Exception e) { Log.Write("读取账号列表失败: " + e.Message); }

        // 内置主账号（复用原有目录，保证关掉轮换后一切照旧）
        if (!list.Any(s => s.IsPrimary))
        {
            var primary = new AccountSlot { Id = "primary", Name = "主账号", IsPrimary = true, Enabled = true, Note = "使用原有登录数据" };
            list.Insert(0, primary);
        }
        lock (_lk) { _slots.Clear(); _slots.AddRange(list); }
        Log.Write("账号池已载入 " + list.Count + " 个账号: " + string.Join(", ", list.Select(s => s.Name)));
        Notify();
    }

    public void SaveToPrefs()
    {
        try
        {
            var arr = new JArr();
            foreach (var s in Snapshot()) arr.Add(s.ToJson());
            Prefs.AccountsJson = arr.ToJson();
        }
        catch (Exception e) { Log.Write("保存账号列表失败: " + e.Message); }
    }

    /// <summary>把已构造好的账号槽放进池子（UI / 真机测试用）。</summary>
    public void AttachAccount(AccountSlot s)
    {
        if (s == null) return;
        lock (_lk) { if (!_slots.Any(x => x.Id == s.Id)) _slots.Add(s); }
        Notify();
    }

    /// <summary>测试用：清空池子。</summary>
    public void ClearForTest()
    {
        lock (_lk) _slots.Clear();
        Notify();
    }

    public AccountSlot Add(string name)
    {
        var s = new AccountSlot
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
            Name = string.IsNullOrEmpty(name) ? "新账号" : name,
            Enabled = true,
        };
        lock (_lk) _slots.Add(s);
        SaveToPrefs();
        Log.Write("新增账号槽 " + s.Id + "（" + s.Name + "）");
        Notify();
        return s;
    }

    public void Remove(string id)
    {
        AccountSlot s = null;
        lock (_lk)
        {
            s = _slots.FirstOrDefault(x => x.Id == id);
            if (s != null && s.IsPrimary) return;   // 主账号不可删
            if (s != null) _slots.Remove(s);
        }
        if (s != null) { SaveToPrefs(); Log.Write("删除账号槽 " + id); Notify(); }
    }

    /* ================= 调度 ================= */

    /// <summary>可用（参与调度）的账号。</summary>
    public List<AccountSlot> Available()
    {
        lock (_lk) return _slots.Where(s => s.Enabled && s.LoggedIn && !s.InCooldown).ToList();
    }

    public bool AnyUsable
    {
        get { lock (_lk) return _slots.Any(s => s.Enabled && s.LoggedIn); }
    }

    /// <summary>最短的冷却剩余秒数（全部冷却时用于 Retry-After）。</summary>
    public int MinCooldownLeftSec()
    {
        lock (_lk)
        {
            var c = _slots.Where(s => s.Enabled && s.LoggedIn && s.InCooldown).ToList();
            return c.Count == 0 ? 0 : c.Min(s => s.CooldownLeftSec);
        }
    }

    /// <summary>
    /// 选一个账号来发送。
    /// 返回 null 表示没有可用账号（调用方应返回 503/429）。
    /// needNewChat 为 true 表示这个账号本轮会话已用满，需要先新开对话。
    /// </summary>
    public AccountSlot Acquire(out bool needNewChat)
    {
        needNewChat = false;
        int limit = Math.Max(1, Prefs.SessionSendLimit);

        List<AccountSlot> avail;
        lock (_lk) avail = _slots.Where(s => s.Enabled && s.LoggedIn && !s.InCooldown).ToList();
        if (avail.Count == 0) return null;

        // 优先：本轮还没用满的账号；其次：最久没被使用过的账号（轮流）
        var pick = avail
            .OrderBy(s => s.SentInSession >= limit ? 1 : 0)
            .ThenBy(s => s.LastUsedMs)
            .First();

        if (pick.SentInSession >= limit) needNewChat = true;
        return pick;
    }

    /// <summary>本轮会话已满 → 新开对话并重置计数（由调用方在真正发送前落库）。</summary>
    public void ResetSession(AccountSlot s, string sessionId)
    {
        if (s == null) return;
        s.SentInSession = 0;
        s.SessionId = sessionId ?? "";
        Log.Write("账号 " + s.Name + " 会话计数已重置（新会话 " + (sessionId ?? "").Substring(0, Math.Min(8, (sessionId ?? "").Length)) + "）");
    }

    public void OnSent(AccountSlot s, bool ok, string error, string sessionId)
    {
        if (s == null) return;
        s.LastUsedMs = AccountSlot.NowMs;
        s.TotalCalls++;
        if (ok) s.OkCalls++; else s.LastError = error ?? "";
        if (!string.IsNullOrEmpty(sessionId)) s.SessionId = sessionId;

        bool limited = LooksRateLimited(error);
        if (limited)
        {
            s.RateLimitedCount++;
            s.CooldownUntil = AccountSlot.NowMs + Math.Max(1, Prefs.CooldownMinutes) * 60_000L;
            s.SentInSession = 0;   // 冷却结束后按新会话对待
            Log.Write("!! 账号 " + s.Name + " 命中「消息发送频繁」→ 冷却 " + Prefs.CooldownMinutes + " 分钟");
        }
        else
        {
            s.SentInSession++;
        }
        SaveToPrefs();
        Notify();
    }

    public static bool LooksRateLimited(string err)
    {
        if (string.IsNullOrEmpty(err)) return false;
        string e = err.ToLowerInvariant();
        return e.Contains("频繁") || e.Contains("稍后") || e.Contains("发送太快") || e.Contains("太快了")
            || e.Contains("too many") || e.Contains("rate limit") || e.Contains("ratelimit")
            || e.Contains("frequent") || e.Contains("slow down") || e.Contains("消息发送");
    }

    public void MarkCooldown(AccountSlot s, int minutes, string why)
    {
        if (s == null) return;
        s.CooldownUntil = AccountSlot.NowMs + Math.Max(1, minutes) * 60_000L;
        s.SentInSession = 0;
        s.Note = why ?? "";
        Log.Write("账号 " + s.Name + " 进入冷却 " + minutes + " 分钟（" + why + "）");
        SaveToPrefs();
        Notify();
    }

    public void ClearCooldown(AccountSlot s)
    {
        if (s == null) return;
        s.CooldownUntil = 0;
        SaveToPrefs();
        Notify();
    }

    /// <summary>统计文本（控制台展示）。</summary>
    public string SummaryText()
    {
        var all = Snapshot();
        if (all.Count == 0) return "—";
        var usable = all.Count(s => s.Enabled && s.LoggedIn && !s.InCooldown);
        var cooling = all.Count(s => s.Enabled && s.LoggedIn && s.InCooldown);
        var notLogin = all.Count(s => s.Enabled && !s.LoggedIn);
        var sb = new System.Text.StringBuilder();
        sb.Append("共 ").Append(all.Count).Append(" 个 · 就绪 ").Append(usable);
        if (cooling > 0) sb.Append(" · 冷却 ").Append(cooling);
        if (notLogin > 0) sb.Append(" · 未登录 ").Append(notLogin);
        return sb.ToString();
    }
}
