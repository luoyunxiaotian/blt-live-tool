using System.Text.Json;
using System.Text.Json.Nodes;

namespace BiLi_live_Tool.Services;

/// <summary>
/// 公告推送客户端（通道）。对应 Cloudflare 上的 cloudflare/announce-worker.mjs。
///
/// 工作方式：后台按服务端给的节奏轮询 <c>announceUrl</c>（默认 5 分钟，紧急模式 60 秒），
/// 带上本机 uid / 版本 / 通道（maui）让服务端做定向过滤，再在本地做时间去重：
///   • normal 普通通知 —— 右下角卡片，点「知道了」后按 id 记已读，不再打扰
///   • sticky 持续通知 —— 顶部常驻横幅，直到过期或作者下架
///   • ack    强通知   —— 全屏模态，必须点「我已阅读并确认」（soft 可稍后，下次启动再弹）
///
/// 断网/被墙时静默降级：用本地缓存的最后一份内容；连续失败指数退避（最长 30 分钟）。
/// 这个通道只在 0.1.3 植入一次，之后作者在控制台发公告即可，无需更新应用。
/// </summary>
public sealed class AnnouncementService : IDisposable
{
    public sealed record Item(
        string Id, string Type, string Level, string Title, string Body,
        string PublishAt, string ExpireAt, bool RepeatUntilExpire, string AckMode,
        List<(string Label, string Kind, string Url)> Actions);

    public sealed record Visible(Item Item, bool Sticky, bool Ack, bool Card);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly AppConfig _config;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _read = new();      // id → 已读时间
    private readonly Dictionary<string, string> _acked = new();     // id → 确认时间
    private List<Item> _items = new();
    private Visible? _pendingAck;
    private int _pollSeconds = 300;
    private string _etag = "";
    private int _failStreak;
    private DateTimeOffset _nextPoll = DateTimeOffset.MinValue;
    private CancellationTokenSource? _cts;
    private string _status = "未启动";

    public AnnouncementService(AppConfig config) => _config = config;

    public event Action? Changed;

    public string Status { get { lock (_lock) return _status; } }

    /// <summary>当前要显示的公告：常驻横幅 + 待确认强通知 + 角落卡片。</summary>
    public (List<Visible> Sticky, Visible? Ack, List<Visible> Cards) Snapshot()
    {
        lock (_lock)
        {
            var sticky = new List<Visible>();
            var cards = new List<Visible>();
            foreach (var it in _items)
            {
                var v = Kind(it);
                if (v.Sticky) sticky.Add(v);
                else if (v.Card) cards.Add(v);
            }
            if (sticky.Count > 3) sticky = sticky.GetRange(0, 3);
            if (cards.Count > 3) cards = cards.GetRange(0, 3);
            return (sticky, _pendingAck, cards);
        }
    }

    public bool Enabled
    {
        get
        {
            var node = _config.GetNode("announceEnabled");
            return node is not JsonValue v || !v.TryGetValue<bool>(out var on) || on;
        }
    }

    public string Url
    {
        get
        {
            var n = _config.GetNode("announceUrl");
            var s = n is JsonValue v && v.TryGetValue<string>(out var u) ? u ?? "" : "";
            return s.Trim();
        }
    }

    private string FallbackUrl
    {
        get
        {
            var n = _config.GetNode("announceFallbackUrl");
            return n is JsonValue v && v.TryGetValue<string>(out var u) ? (u ?? "").Trim() : "";
        }
    }

    public void Start()
    {
        if (_cts != null) return;
        LoadState();
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        await RefreshAsync(false, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            var wait = TimeSpan.FromSeconds(Math.Clamp(_pollSeconds, 60, 1800));
            if (_failStreak > 0)
                wait = TimeSpan.FromSeconds(Math.Min(1800, 60 * Math.Pow(2, Math.Min(_failStreak, 5))));
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch { return; }
            await RefreshAsync(false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>拉取公告。<paramref name="force"/> true 时忽略 ETag 并重置退避。</summary>
    public async Task<JsonObject> RefreshAsync(bool force, CancellationToken ct = default)
    {
        var url = Url;
        if (!Enabled) { lock (_lock) { _items = new(); _status = "公告已关闭（config.announceEnabled=false）"; } Changed?.Invoke(); return new JsonObject { ["ok"] = false, ["error"] = "disabled" }; }
        if (url.Length == 0) { lock (_lock) _status = "未配置 announceUrl"; return new JsonObject { ["ok"] = false, ["error"] = "no-url" }; }

        var uid = _config.Uid;
        var sep = url.Contains('?') ? '&' : '?';
        var target = $"{url}{sep}uid={Uri.EscapeDataString(uid)}&v={Uri.EscapeDataString(KestrelHost.VersionText)}&ch=maui";
        var sources = new List<string> { target };
        if (FallbackUrl.Length > 0) sources.Add($"{FallbackUrl}{(FallbackUrl.Contains('?') ? '&' : '?')}uid={Uri.EscapeDataString(uid)}&v={Uri.EscapeDataString(KestrelHost.VersionText)}&ch=maui");

        string? body = null;
        string lastErr = "";
        foreach (var src in sources)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, src);
                if (!force && _etag.Length > 0) req.Headers.TryAddWithoutValidation("If-None-Match", _etag);
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    lock (_lock) { _failStreak = 0; _status = $"已是最新（304，{_items.Count} 条）"; }
                    return new JsonObject { ["ok"] = true, ["notModified"] = true };
                }
                if (!resp.IsSuccessStatusCode) { lastErr = $"HTTP {(int)resp.StatusCode}"; continue; }
                _etag = resp.Headers.ETag?.Tag ?? "";
                body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) { lastErr = ex.Message; }
        }

        if (body == null)
        {
            lock (_lock)
            {
                _failStreak++;
                _status = $"拉取失败（连续 {_failStreak} 次）：{lastErr}";
            }
            ServiceLog.Warn("公告", $"拉取失败：{lastErr}（使用本地缓存）");
            return new JsonObject { ["ok"] = false, ["error"] = lastErr };
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var items = new List<Item>();
            if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var actions = new List<(string, string, string)>();
                    if (e.TryGetProperty("actions", out var ac) && ac.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in ac.EnumerateArray())
                            actions.Add((Str(a, "label"), Str(a, "kind"), Str(a, "url")));
                    }
                    items.Add(new Item(
                        Str(e, "id"), Str(e, "type"), Str(e, "level"), Str(e, "title"), Str(e, "body"),
                        Str(e, "publishAt"), Str(e, "expireAt"),
                        e.TryGetProperty("repeatUntilExpire", out var rp) && rp.ValueKind == JsonValueKind.True,
                        Str(e, "ackMode").Length > 0 ? Str(e, "ackMode") : "soft",
                        actions));
                }
            }
            var poll = root.TryGetProperty("pollAfterSeconds", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetInt32() : 300;
            var serverTime = root.TryGetProperty("serverTime", out var st2) && st2.ValueKind == JsonValueKind.Number ? st2.GetInt64() : 0;
            if (serverTime > 0) _clockSkew = serverTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            SaveCache(body);
            lock (_lock)
            {
                _items = items;
                _pollSeconds = Math.Clamp(poll, 60, 1800);
                _failStreak = 0;
                _status = $"已拉取 {items.Count} 条（下次 {_pollSeconds}s）";
                PickPendingAck();
            }
            ServiceLog.Info("公告", $"已拉取 {items.Count} 条公告（通道 maui，版本 {KestrelHost.VersionText}）");
            Changed?.Invoke();
            return new JsonObject { ["ok"] = true, ["count"] = items.Count };
        }
        catch (Exception ex)
        {
            lock (_lock) _status = "解析失败：" + ex.Message;
            return new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }
    }

    private long _clockSkew;

    private Visible Kind(Item it)
    {
        var ack = string.Equals(it.Type, "ack", StringComparison.OrdinalIgnoreCase);
        var sticky = string.Equals(it.Type, "sticky", StringComparison.OrdinalIgnoreCase);
        bool read, acked;
        lock (_lock) { read = _read.ContainsKey(it.Id); acked = _acked.ContainsKey(it.Id); }

        if (ack) return new Visible(it, false, !acked, false);
        if (sticky) return new Visible(it, true, false, false);
        var showCard = it.RepeatUntilExpire || !read;
        return new Visible(it, false, false, showCard);
    }

    /// <summary>选出一条待强确认的公告（critical 优先，然后按 id 稳定排序）。</summary>
    private void PickPendingAck()
    {
        var pending = _items
            .Where(i => string.Equals(i.Type, "ack", StringComparison.OrdinalIgnoreCase) && !_acked.ContainsKey(i.Id))
            .OrderByDescending(i => string.Equals(i.Level, "critical", StringComparison.OrdinalIgnoreCase))
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();
        _pendingAck = pending.Count > 0 ? new Visible(pending[0], false, true, false) : null;
    }

    public void Dismiss(string id)
    {
        lock (_lock)
        {
            _read[id] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SaveStateLocked();
        }
        _ = ReceiptAsync(id, "seen");
        Changed?.Invoke();
    }

    public void Ack(string id)
    {
        lock (_lock)
        {
            _acked[id] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SaveStateLocked();
            PickPendingAck();
        }
        _ = ReceiptAsync(id, "ack");
        Changed?.Invoke();
    }

    /// <summary>已读/确认回执（尽力而为，失败忽略）。</summary>
    private async Task ReceiptAsync(string id, string kind)
    {
        var url = Url;
        if (url.Length == 0) return;
        try
        {
            var receipt = url.EndsWith("/announcements", StringComparison.OrdinalIgnoreCase)
                ? url[..^"/announcements".Length] + "/receipt"
                : url + "/receipt";
            var payload = JsonSerializer.Serialize(new { id, kind, uid = _config.Uid, v = KestrelHost.VersionText });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(receipt, content).ConfigureAwait(false);
        }
        catch { }
    }

    public JsonObject StatusJson()
    {
        lock (_lock)
        {
            return new JsonObject
            {
                ["enabled"] = Enabled,
                ["url"] = Url,
                ["status"] = _status,
                ["items"] = _items.Count,
                ["read"] = _read.Count,
                ["acked"] = _acked.Count,
                ["pendingAck"] = _pendingAck?.Item.Id ?? "",
                ["pollSeconds"] = _pollSeconds,
                ["failStreak"] = _failStreak,
            };
        }
    }

    // ---------------- 本地持久化 ----------------
    private static string StatePath => Path.Combine(AppContext.BaseDirectory, "data", "announcements-state.json");
    private static string CachePath => Path.Combine(AppContext.BaseDirectory, "data", "announcements-cache.json");

    private void LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var root = JsonNode.Parse(File.ReadAllText(StatePath)) as JsonObject;
                if (root?["read"] is JsonObject rd)
                    foreach (var kv in rd) _read[kv.Key] = kv.Value?.ToString() ?? "";
                if (root?["ack"] is JsonObject ak)
                    foreach (var kv in ak) _acked[kv.Key] = kv.Value?.ToString() ?? "";
            }
            if (File.Exists(CachePath))
            {
                var body = File.ReadAllText(CachePath);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var items = new List<Item>();
                    foreach (var e in arr.EnumerateArray())
                    {
                        var actions = new List<(string, string, string)>();
                        if (e.TryGetProperty("actions", out var ac) && ac.ValueKind == JsonValueKind.Array)
                            foreach (var a in ac.EnumerateArray()) actions.Add((Str(a, "label"), Str(a, "kind"), Str(a, "url")));
                        items.Add(new Item(Str(e, "id"), Str(e, "type"), Str(e, "level"), Str(e, "title"), Str(e, "body"),
                            Str(e, "publishAt"), Str(e, "expireAt"),
                            e.TryGetProperty("repeatUntilExpire", out var rp) && rp.ValueKind == JsonValueKind.True,
                            Str(e, "ackMode").Length > 0 ? Str(e, "ackMode") : "soft", actions));
                    }
                    lock (_lock) { _items = items; PickPendingAck(); }
                }
            }
        }
        catch { }
    }

    private void SaveStateLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var obj = new JsonObject
            {
                ["read"] = new JsonObject(_read.Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value))),
                ["ack"] = new JsonObject(_acked.Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value))),
            };
            File.WriteAllText(StatePath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void SaveCache(string body)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, body);
        }
        catch { }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public void Dispose() => Stop();
}
