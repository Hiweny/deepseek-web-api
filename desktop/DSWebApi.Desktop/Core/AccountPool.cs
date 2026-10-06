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
    public int SentInSession;           // 当前这轮对话里已占用几个名额（含在途；到上限就换下一个账号）
    public int Reserved;                // >0 表示有请求正在用这个账号（并发分散用）
    public string SessionId = "";
    public bool NeedNewChat;            // true = 下次用这个账号前，先「新开对话」
    public int RoundNo = 1;             // 该账号当前处在第几轮
    public long SessionTokens;          // 该账号当前对话的累计估算 token（用于上下文超限预防）
    public string TrackedSessionId = "";// SessionTokens 对应的会话
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
            if (LoggedIn) return (NeedNewChat ? "待新对话 · " : "已发 ") + SentInSession + "/" + Prefs.SessionSendLimit
                    + (Reserved > 0 ? "（在用）" : "");
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
/// 轮换策略（按用户要求：严格顺序、不是每小时配额）：
///   1. 账号**严格按顺序** 1 → 2 → 3 → … 轮流，不按"用量/空闲时间"打分；
///   2. 同一个账号在**同一个对话里最多连发 N 次**（默认 2），发满就换下一个账号；
///   3. 所有账号都轮完一遍 → 进入下一轮，**每个账号都要新开对话**（不在旧对话上继续，避免串味/超上下文）；
///   4. 某账号出现「消息发送频繁」→ 进入冷却（默认 30 分钟），期间跳过它继续服务；
///   5. 所有账号都在冷却 → 返回 429 + 冷却剩余时间，不硬打上游。
/// </summary>
public sealed class AccountPool
{
    public static readonly AccountPool I = new AccountPool();

    private readonly object _lk = new object();
    private readonly List<AccountSlot> _slots = new List<AccountSlot>();
    private int _cursor;        // 下一个要用的账号在 _slots 里的下标（严格顺序轮换）
    private int _cycleNo = 1;   // 当前第几轮（一轮 = 每个账号各承接口一次）

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
        lock (_lk) { _slots.Clear(); _cursor = 0; _cycleNo = 1; }
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
            if (s != null)
            {
                int at = _slots.IndexOf(s);
                _slots.Remove(s);
                if (at >= 0 && at < _cursor) _cursor--;      // 保持"下一棒"不被跳过
                if (_cursor >= _slots.Count) _cursor = 0;
            }
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
        int limit = Limit();
        lock (_lk)
        {
            int n = _slots.Count;
            if (n == 0) return null;
            if (_cursor < 0 || _cursor >= n) _cursor = 0;

            // ① 续用「当前这一棒」：可用、还有余额、当前没有在途 → 同一个账号连发 N 次
            var cur = _slots[_cursor];
            if (Usable(cur) && cur.SentInSession < limit && cur.Reserved == 0)
                return ReserveLocked(cur, ref needNewChat);

            // ② 当前这一棒忙/在途 → 找别的「空闲（无在途）且还有余额」的账号
            //    （并发请求因此会被分散到不同账号，而不是挤在同一个号上）
            for (int k = 1; k < n; k++)
            {
                int idx = (_cursor + k) % n;
                var s2 = _slots[idx];
                if (!Usable(s2)) continue;
                if (s2.SentInSession >= limit) continue;
                if (s2.Reserved > 0) continue;
                _cursor = idx;
                return ReserveLocked(s2, ref needNewChat);
            }

            // ③ 其它账号都不空闲，但当前账号还有余额 → 继续用它（排队，不越界）
            if (Usable(cur) && cur.SentInSession < limit)
                return ReserveLocked(cur, ref needNewChat);

            // ④ 可用的账号本轮余额都耗尽了 → 开新一轮：全员新开对话，再取
            if (!_slots.Any(Usable)) return null;
            StartNewCycleLocked();
            for (int k = 0; k < n; k++)
            {
                var s3 = _slots[k];
                if (!Usable(s3)) continue;
                _cursor = k;
                return ReserveLocked(s3, ref needNewChat);
            }
            return null;
        }
    }

    /// <summary>就地预占一个名额（调用方必须持有 _lk）。</summary>
    private AccountSlot ReserveLocked(AccountSlot s, ref bool needNewChat)
    {
        s.SentInSession++;              // ★ 预占名额：并发时保证分给不同账号
        s.Reserved++;
        s.RoundNo = _cycleNo;
        if (s.NeedNewChat) { needNewChat = true; s.NeedNewChat = false; }
        return s;
    }
    /// <summary>当前连发上限（每个账号在一个对话里最多发几次）。</summary>
    public static int Limit()
    {
        int v = Prefs.SessionSendLimit;
        return v < 1 ? 1 : (v > 50 ? 50 : v);
    }

    /// <summary>是否可用（参与调度）。</summary>
    private static bool Usable(AccountSlot s) => s != null && s.Enabled && s.LoggedIn && !s.InCooldown;

    /// <summary>开新一轮：所有账号计数清零，并要求各自新开对话。</summary>
    private void StartNewCycleLocked()
    {
        _cycleNo++;
        _cursor = 0;
        foreach (var s in _slots)
        {
            s.SentInSession = 0;
            s.Reserved = 0;
            s.RoundNo = _cycleNo;
            if (Usable(s)) s.NeedNewChat = true;
        }
        Log.Write("轮换：所有账号都轮过一遍 → 进入第 " + _cycleNo + " 轮，各账号将新开对话");
    }

    /// <summary>第几轮（UI 显示用）。</summary>
    public int CycleNo { get { lock (_lk) return _cycleNo; } }

    /// <summary>当前被预占（在途）的名额数（UI 显示并发情况）。</summary>
    public int ReservedCount { get { lock (_lk) return _slots.Sum(s => s.SentInSession); } }

    /// <summary>把某个账号的一个预占名额退回（发送前就失败时用）。</summary>
    public void ReleaseReservation(AccountSlot s)
    {
        if (s == null) return;
        lock (_lk) { if (s.Reserved > 0) s.Reserved--; if (s.SentInSession > 0) s.SentInSession--; s.NeedNewChat = true; }
        Notify();
    }

    /// <summary>下一棒是谁（UI 显示用）。</summary>
    public string NextName()
    {
        lock (_lk)
        {
            int n = _slots.Count;
            if (n == 0) return "—";
            if (_cursor < 0 || _cursor >= n) _cursor = 0;
            return _slots[_cursor].Name;
        }
    }

    /// <summary>本轮会话已满 → 新开对话并重置计数（由调用方在真正发送前落库）。</summary>
    public void MarkChatOpened(AccountSlot s, string sessionId)
    {
        if (s == null) return;
        // 注意：不再清零 SentInSession —— 本次名额已在 Acquire 阶段预占（并发安全）
        s.NeedNewChat = false;
        s.SessionTokens = 0;
        if (!string.IsNullOrEmpty(sessionId)) { s.SessionId = sessionId; s.TrackedSessionId = sessionId; }
        Log.Write("账号 " + s.Name + " 已新开对话（会话 " + Short8(sessionId) + "）");
    }

    private static string Short8(string s2)
    {
        if (string.IsNullOrEmpty(s2)) return "—";
        return s2.Length <= 8 ? s2 : s2.Substring(0, 8);
    }

    public void OnSent(AccountSlot s, bool ok, string error, string sessionId)
    {
        if (s == null) return;
        s.LastUsedMs = AccountSlot.NowMs;
        if (s.Reserved > 0) s.Reserved--;
        s.TotalCalls++;
        if (ok) s.OkCalls++; else s.LastError = error ?? "";
        if (!string.IsNullOrEmpty(sessionId)) s.SessionId = sessionId;

        bool limited = LooksRateLimited(error);
        if (limited)
        {
            s.RateLimitedCount++;
            s.CooldownUntil = AccountSlot.NowMs + Math.Max(1, Prefs.CooldownMinutes) * 60_000L;
            s.SentInSession = 0;   // 冷却结束后按新对话对待
            s.Reserved = 0;
            s.NeedNewChat = true;
            Log.Write("!! 账号 " + s.Name + " 命中「消息发送频繁」→ 冷却 " + Prefs.CooldownMinutes + " 分钟");
        }
        // 成功 / 其它失败：名额在 Acquire 预占时已计入，这里不再改动（并发安全）
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
        int usable = all.Count(s => s.Enabled && s.LoggedIn && !s.InCooldown);
        int cooling = all.Count(s => s.Enabled && s.LoggedIn && s.InCooldown);
        int notLogin = all.Count(s => s.Enabled && !s.LoggedIn);
        var sb = new System.Text.StringBuilder();
        sb.Append("第 ").Append(CycleNo).Append(" 轮 · 就绪 ").Append(usable).Append("/").Append(all.Count);
        if (cooling > 0) sb.Append(" · 冷却 ").Append(cooling);
        if (notLogin > 0) sb.Append(" · 未登录 ").Append(notLogin);
        return sb.ToString();
    }
}
