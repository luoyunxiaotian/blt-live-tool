using System.Text.Json.Nodes;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Port of lib/gift-merge.js: per-user gift aggregation with a refreshable
/// silence window; emits one gifts_merged event per user batch.
/// </summary>
public sealed class GiftAggregator
{
    public event Action<LiveEvent>? OnMerged;

    private readonly object _lock = new();
    private readonly Dictionary<string, UserBucket> _users = new();
    private long _waitMs = 3000;

    private sealed class UserBucket
    {
        public string Uname = "";
        public List<GiftItem> Gifts = new();
        public System.Threading.Timer? Timer;
    }

    public void SetWaitMs(long ms)
    {
        if (ms > 0) _waitMs = ms;
    }

    public void Push(LiveEvent ev)
    {
        if (ev.Type != "gifts" || string.IsNullOrEmpty(ev.Uid)) return;
        System.Threading.Timer? timerToChange = null;
        lock (_lock)
        {
            if (!_users.TryGetValue(ev.Uid, out var u)) _users[ev.Uid] = u = new UserBucket();
            if (!string.IsNullOrWhiteSpace(ev.Uname)) u.Uname = ev.Uname.Trim();
            var name = string.IsNullOrEmpty(ev.GiftName) ? "礼物" : ev.GiftName;
            var num = Math.Max(1, ev.Num);
            var found = u.Gifts.FirstOrDefault(g => g.GiftName == name);
            if (found != null)
            {
                var idx = u.Gifts.IndexOf(found);
                u.Gifts[idx] = found with { Num = found.Num + num, Price = ev.Price > 0 ? ev.Price : found.Price };
            }
            else
            {
                u.Gifts.Add(new GiftItem(name, num, ev.Price));
            }
            // Refresh the user's silence window.
            timerToChange = u.Timer;
            var uid = ev.Uid;
            u.Timer = new System.Threading.Timer(_ => Flush(uid), null, _waitMs, Timeout.Infinite);
        }
        timerToChange?.Dispose();
    }

    private void Flush(string uid)
    {
        UserBucket u;
        lock (_lock)
        {
            if (!_users.TryGetValue(uid, out var bucket)) return;
            _users.Remove(uid);
            u = bucket;
        }
        u.Timer?.Dispose();
        if (u.Gifts.Count == 0) return;
        long totalNum = 0, totalCoin = 0;
        foreach (var g in u.Gifts) { totalNum += g.Num; totalCoin += g.Num * g.Price; }
        var ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        OnMerged?.Invoke(new LiveEvent
        {
            Type = "gifts_merged",
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Ts = ts,
            Uid = uid,
            Uname = string.IsNullOrEmpty(u.Uname) ? "用户" + uid : u.Uname,
            Gifts = u.Gifts,
            TotalNum = totalNum,
            TotalCoin = totalCoin,
            Value = Math.Round(totalCoin / 1000.0 * 100) / 100,
        });
    }

    public void Reset()
    {
        List<UserBucket> all;
        lock (_lock)
        {
            all = _users.Values.ToList();
            _users.Clear();
        }
        foreach (var u in all) u.Timer?.Dispose();
    }
}

/// <summary>
/// Port of lib/autodanmu.js: welcome-on-enter (60s per-uid cooldown), follow
/// thanks (per-session dedup, FIFO cap 5000), gift thanks (consumes merged
/// events) and periodic timer danmaku. Sends are serialized with a gap.
/// </summary>
public sealed class AutoDanmu
{
    private const int FollowCap = 5000;

    private readonly object _lock = new();
    private readonly Dictionary<string, long> _lastWelcome = new();
    private readonly HashSet<string> _followedUids = new();
    private readonly List<string> _followOrder = new();
    private readonly Func<long, string, Task> _send;   // (roomId, msg)
    private readonly Func<bool> _isReady;
    private readonly Func<(long RoomId, string Cookie)> _context;

    private JsonObject _welcome = new(), _thank = new(), _timerCfg = new(), _follow = new();
    private long _lastSend;
    private int _followCount;
    private int _timerFired;
    private string _lastSendText = "";
    private string _lastSendResult = "";
    private string _lastSendAt = "";
    private CancellationTokenSource? _timerCts;

    public AutoDanmu(Func<long, string, Task> send, Func<bool> isReady, Func<(long RoomId, string Cookie)> context)
    {
        _send = send;
        _isReady = isReady;
        _context = context;
    }

    // ---- config ----

    public void UpdateConfig(JsonNode? autoDanmu)
    {
        lock (_lock)
        {
            _welcome = Section(autoDanmu, "welcome");
            _thank = Section(autoDanmu, "thank");
            _timerCfg = Section(autoDanmu, "timer");
            _follow = Section(autoDanmu, "follow");
        }
        RestartTimer();
    }

    private static JsonObject Section(JsonNode? parent, string key)
        => (parent as JsonObject)?.TryGetPropertyValue(key, out var v) == true && v is JsonObject o
            ? o : new JsonObject();

    private static bool Flag(JsonObject o, string key, bool def = false)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<bool>(out var b) ? b : def;

    private static long Num(JsonObject o, string key, long def = 0)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<long>(out var l) ? l : def;

    private static List<string> StrArray(JsonObject o, string key)
    {
        if (o.TryGetPropertyValue(key, out var v) && v is JsonArray arr)
            return arr.Where(x => x != null).Select(x => x!.GetValue<string>() ?? "").Where(s => s.Trim().Length > 0).ToList();
        return new List<string>();
    }

    private static string Fill(string tpl, Dictionary<string, string> vars)
    {
        foreach (var (k, v) in vars) tpl = tpl.Replace("{" + k + "}", v);
        return tpl;
    }

    private static string Pick(List<string> list, Random rand)
        => list.Count > 0 ? list[rand.Next(list.Count)] : "";

    // ---- events ----

    public void OnEvent(LiveEvent ev)
    {
        if (ev.Type == "interact")
        {
            if (ev.MsgType == 2)
            {
                var r = HandleFollow(ev);
                if (r == "sent" || r == "skip") return;
            }
            Welcome(ev);
        }
        else if (ev.Type == "guard")
        {
            Welcome(ev);   // guard purchase/upgrade → guard-specific welcome
        }
        else if (ev.Type == "gifts_merged")
        {
            Thank(ev);
        }
    }

    private string HandleFollow(LiveEvent ev)
    {
        List<string> texts;
        long rate;
        lock (_lock)
        {
            if (!Flag(_follow, "enabled")) return "off";
            var uid = (ev.Uid ?? "").Trim();
            if (uid.Length == 0) return "skip";
            if (_followedUids.Contains(uid)) return "skip";
            _followedUids.Add(uid);
            _followOrder.Add(uid);
            if (_followOrder.Count > FollowCap)
            {
                var old = _followOrder[0];
                _followOrder.RemoveAt(0);
                _followedUids.Remove(old);
            }
            _followCount++;
            rate = Num(_follow, "rate", 1);
            texts = StrArray(_follow, "texts");
            if (texts.Count == 0) texts = new List<string> { "感谢 {uname} 的关注～" };
        }
        var name = string.IsNullOrWhiteSpace(ev.Uname) ? "用户" + ev.Uid : ev.Uname;
        var msg = Fill(Pick(texts, Random.Shared), new Dictionary<string, string> { ["uname"] = name, ["uid"] = ev.Uid ?? "" });
        QueueSend(msg, rate > 0 ? rate * 1000 : 150);
        return "sent";
    }

    public object FollowStats()
    {
        lock (_lock)
            return new { count = _followCount, tracked = _followedUids.Count, cap = FollowCap };
    }

    private void Welcome(LiveEvent ev)
    {
        List<string> texts, guardTexts;
        long honorMin, medalMin, rate;
        lock (_lock)
        {
            if (!Flag(_welcome, "enabled")) return;
            honorMin = Num(_welcome, "honorLevelMin", 1);
            medalMin = Num(_welcome, "fanMedalMin", 1);
            rate = Num(_welcome, "rate", 1);
            texts = StrArray(_welcome, "texts");
            guardTexts = StrArray(_welcome, "guardTexts");
            if (texts.Count == 0) texts = new List<string> { "欢迎 {uname} 来到直播间～" };
            if (guardTexts.Count == 0) guardTexts = new List<string> { "欢迎 {uname} 舰长光临直播间！" };
        }
        // Original semantics: min > 0 filters users at or below that level.
        if (honorMin > 0 && ev.HonorLevel <= honorMin) return;
        if (medalMin > 0 && ev.MedalLevel <= medalMin) return;
        lock (_lock)
        {
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (_lastWelcome.TryGetValue(ev.Uid, out var last) && now - last < 60000) return;
            _lastWelcome[ev.Uid] = now;
        }
        var isGuard = ev.IsGuard || ev.Type == "guard";
        var uname = string.IsNullOrWhiteSpace(ev.Uname) ? (ev.Uid.Length > 0 ? "用户" + ev.Uid : "观众") : ev.Uname;
        var pool = isGuard ? guardTexts : texts;
        var tpl = Pick(pool, Random.Shared);
        if (tpl.Length == 0) tpl = isGuard ? guardTexts[0] : texts[0];
        var msg = Fill(tpl, new Dictionary<string, string> { ["uname"] = uname, ["uid"] = ev.Uid, ["levelName"] = ev.LevelName });
        var gap = isGuard ? 1200 : (rate > 0 ? rate * 1000 : 150);
        QueueSend(msg, gap);
    }

    private void Thank(LiveEvent ev)
    {
        List<string> pool;
        lock (_lock)
        {
            if (!Flag(_thank, "enabled", true)) return;
            pool = StrArray(_thank, "texts");
            if (pool.Count == 0) pool = new List<string> { "感谢 {uname} 送出的 {giftSummary}～" };
        }
        var list = ev.Gifts ?? new List<GiftItem>();
        if (list.Count == 0) return;
        var uname = string.IsNullOrWhiteSpace(ev.Uname) ? (ev.Uid.Length > 0 ? "用户" + ev.Uid : "观众") : ev.Uname;
        var summary = GiftSummaryText(list);
        var single = list.Count == 1 ? list[0] : null;
        var vars = new Dictionary<string, string>
        {
            ["uname"] = uname,
            ["giftSummary"] = summary,
            ["giftName"] = single != null ? single.GiftName : summary,
            ["num"] = (single != null ? single.Num : ev.TotalNum).ToString(),
        };
        var msg = Fill(Pick(pool, Random.Shared), vars);
        QueueSend(msg, 1200);
    }

    // 「小花花 5个 牛哇牛哇 7个」, truncated with 「… 等N种礼物」 beyond the danmu length cap.
    private static string GiftSummaryText(List<GiftItem> list, int cap = 40)
    {
        var items = list.Select(g => (g.GiftName.Length > 0 ? g.GiftName : "礼物") + " " + Math.Max(1, g.Num) + "个").ToList();
        if (items.Count == 0) return "";
        var s = string.Join(" ", items);
        if (s.Length <= cap || items.Count == 1) return s;
        var acc = "";
        var i = 0;
        while (i < items.Count)
        {
            var next = acc.Length > 0 ? acc + " " + items[i] : items[i];
            if ((next + " 等" + (items.Count - i) + "种礼物").Length > cap) break;
            acc = next;
            i++;
        }
        if (acc.Length == 0) acc = items[0][..Math.Min(cap, items[0].Length)];
        if (i < items.Count) acc += " 等" + (items.Count - i) + "种礼物";
        return acc;
    }

    // ---- serialized sending (avoids B站 rate limits, mirrors autodanmu.js _send) ----

    private void QueueSend(string msg, long gapMs)
    {
        if (string.IsNullOrEmpty(msg)) return;
        var gap = Math.Max(150, gapMs);
        long baseAt;
        lock (_lock)
        {
            baseAt = Math.Max(_lastSend, DateTimeOffset.Now.ToUnixTimeMilliseconds());
            _lastSend = baseAt + gap;
            _lastSendText = msg;
            _lastSendAt = DateTime.Now.ToString("HH:mm:ss");
            _lastSendResult = "排队中";
        }
        var delay = Math.Max(0, (int)(baseAt - DateTimeOffset.Now.ToUnixTimeMilliseconds()));
        var ctx = _context();
        if (ctx.RoomId == 0)
        {
            lock (_lock) _lastSendResult = "未连接直播间";
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay); }
            catch { return; }
            try
            {
                await _send(ctx.RoomId, msg);
                lock (_lock) _lastSendResult = "ok";
            }
            catch (Exception ex)
            {
                // Surface the failure instead of swallowing it: a thank-you that
                // never appears is usually this line (cookie / 风控 / 未开播).
                lock (_lock) _lastSendResult = "失败: " + ex.Message;
            }
        });
    }

    /// <summary>Last auto-danmu send attempt — diagnostics for the panel/debug bridge.</summary>
    public object SendStats()
    {
        lock (_lock)
            return new { lastText = _lastSendText, result = _lastSendResult, at = _lastSendAt, followCount = _followCount };
    }

    // ---- timer danmaku ----

    private void RestartTimer()
    {
        JsonObject cfg;
        lock (_lock) cfg = _timerCfg;
        _timerCts?.Cancel();
        _timerCts = new CancellationTokenSource();
        var ct = _timerCts.Token;
        var enabled = Flag(cfg, "enabled");
        var intervalSec = Math.Max(1, Num(cfg, "intervalSec", 60));
        var texts = StrArray(cfg, "texts");
        if (texts.Count == 0) texts = new List<string> { "欢迎来到直播间，喜欢主播的点点关注～" };
        if (!enabled) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, intervalSec)), ct);
                    if (!_isReady()) continue;
                    var msg = Pick(texts, Random.Shared);
                    if (msg.Length == 0) continue;
                    Interlocked.Increment(ref _timerFired);
                    QueueSend(msg, 1200);
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    public void Reset()
    {
        lock (_lock) _lastWelcome.Clear();
    }
}
