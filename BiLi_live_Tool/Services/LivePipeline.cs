using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Port of the event fan-out in Bin/server.js connectRoom(): every normalized
/// event flows through here — debug stats, recorder, gift aggregation (gifts)
/// / auto danmu (everything else), PK tracking, song request — plus the like
/// task and the multi-room monitor loops.
/// </summary>
public sealed class LivePipeline : IDisposable
{
    private readonly AppConfig _config;
    private readonly EventHub _hub;
    private readonly Recorder _recorder;
    private readonly GiftAggregator _aggregator;
    private readonly AutoDanmu _autoDanmu;
    private readonly SongRequestService _songRequest;
    private readonly TtsHost _tts;
    private readonly CancellationTokenSource _cts = new();

    // debug stats (/api/debug/stats)
    private readonly object _statLock = new();
    private readonly Dictionary<string, (long Count, string Last, string Sample)> _stats = new();

    // pk state
    private readonly object _pkLock = new();
    private bool _pkActive;
    private JsonObject? _pkOpponent;
    private long _pkSince;
    private long _pkVotes;
    private string _pkTitle = "";
    private string _pkCmd = "";
    private CancellationTokenSource? _pkLoopCts;

    // like task
    private readonly object _likeLock = new();
    private bool _likeRunning;
    private long _likeLiked;
    private string _likeError = "";
    private bool _likeStop;

    // room monitor
    private readonly Dictionary<string, JsonObject> _monitorState = new();
    private long _monitorLastFetchAt;

    public LivePipeline(AppConfig config, EventHub hub, Recorder recorder, TtsHost tts)
    {
        _config = config;
        _hub = hub;
        _recorder = recorder;
        _tts = tts;

        _aggregator = new GiftAggregator();
        _aggregator.OnMerged += PublishMerged;

        long RoomId() => long.TryParse(_hub.LastStatus.RealRoomId, out var r) ? r : 0;
        (long RoomId, string Cookie) Ctx() => (RoomId(), _config.Cookie);
        _autoDanmu = new AutoDanmu(
            (roomId, msg) => BiliApi.SendDanmuAsync(roomId, msg, _config.Cookie, CancellationToken.None),
            () => RoomId() != 0,
            Ctx);

        _songRequest = new SongRequestService(config);
    }

    public AutoDanmu AutoDanmu => _autoDanmu;
    public SongRequestService SongRequest => _songRequest;

    public void Start()
    {
        _hub.OnEvent += HandleEvent;
        ApplyConfig();
        _ = MonitorLoopAsync(_cts.Token);
    }

    /// <summary>Re-apply config-dependent subsystems (POST /api/config).</summary>
    public void ApplyConfig()
    {
        _autoDanmu.UpdateConfig(_config.GetNode("autoDanmu"));
        var waitMs = ((_config.GetNode("autoDanmu") as JsonObject)?["thank"] as JsonObject)?
            .TryGetPropertyValue("waitMs", out var wm) == true && wm is JsonValue wv ? wv.GetValue<long?>() ?? 3000 : 3000;
        _aggregator.SetWaitMs(waitMs);
        var engine = (_config.GetNode("tts") as JsonObject)?.TryGetPropertyValue("engine", out var eng) == true && eng is JsonValue ev2
            ? ev2.GetValue<string>() : "edge";
        if (engine == "moss") _tts.EnsureMoss();
    }

    private void PublishMerged(LiveEvent ev) => _hub.Publish(ev);

    private void HandleEvent(LiveEvent ev)
    {
        try
        {
            NoteStat(ev);
            _recorder.HandleEvent(ev);
            if (ev.Type == "gifts")
                _aggregator.Push(ev);   // thanks fire after the silence window via gifts_merged
            else
                _autoDanmu.OnEvent(ev);
            if (ev.Type == "pk")
                _ = HandlePkEventAsync(ev);
            if (ev.Type == "danmu")
                _ = PumpSongRequestAsync(ev);
        }
        catch { /* the pipeline must survive any single handler failure */ }
    }

    private void NoteStat(LiveEvent ev)
    {
        lock (_statLock)
        {
            var (count, _, _) = _stats.TryGetValue(ev.Type, out var s) ? s : (0L, "", "");
            var sample = (ev.Uname + ": " + (ev.Msg.Length > 0 ? ev.Msg : ev.GiftName)).Trim();
            if (sample.Length > 60) sample = sample[..60];
            _stats[ev.Type] = (count + 1, ev.Time, sample);
        }
    }

    public object DebugStats()
    {
        List<object> list;
        lock (_statLock)
            list = _stats.Select(kv => new { type = kv.Key, count = kv.Value.Count, last = kv.Value.Last, sample = kv.Value.Sample })
                .OrderByDescending(x => x.count).Cast<object>().ToList();
        return new { stats = list, connected = _hub.LastStatus.State == "connected", roomId = _config.RoomId };
    }

    /// <summary>Debug injection path: broadcast + aggregation/auto-danmu + song request (no recording), like server.js.</summary>
    public void ProcessDebugEvent(LiveEvent ev)
    {
        NoteStat(ev);
        _hub.Publish(ev);
        if (ev.Type == "gifts") _aggregator.Push(ev);
        else _autoDanmu.OnEvent(ev);
        if (ev.Type == "danmu")
            _ = PumpSongRequestAsync(ev);
    }

    private async Task PumpSongRequestAsync(LiveEvent ev)
    {
        try
        {
            var r = await _songRequest.OnEventAsync(ev, _cts.Token);
            if (r != null) _hub.PublishOutbound("song_request", r);
        }
        catch { }
    }

    // ---------------- PK tracking (port of server.js PK block) ----------------

    private static string? ExtractRoom(JsonNode? data, string myRoom)
    {
        if (data == null) return null;
        var stack = new Stack<JsonNode?>();
        stack.Push(data);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur is not JsonObject obj) continue;
            foreach (var (k, v) in obj)
            {
                if (v == null) continue;
                if (k is "room_id" or "roomid")
                {
                    var s = v is JsonValue val ? val.TryGetValue<string>(out var sv) ? sv : v.ToJsonString().Trim('"') : "";
                    if (s.Length > 0 && s != myRoom) return s;
                }
                if (v is JsonObject or JsonArray) stack.Push(v);
            }
        }
        return null;
    }

    private static long ExtractVotes(JsonNode? data)
    {
        var best = 0L;
        if (data == null) return 0;
        var pattern = new Regex("pk_votes|pk_score|vote|score|gift_amount", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var stack = new Stack<JsonNode?>();
        stack.Push(data);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur is not JsonObject obj) continue;
            foreach (var (k, v) in obj)
            {
                if (v is JsonValue val && val.TryGetValue<long>(out var n) && pattern.IsMatch(k) && n > best) best = n;
                if (v is JsonObject or JsonArray) stack.Push(v);
            }
        }
        return best;
    }

    private object PkStatePayload()
    {
        lock (_pkLock)
            return new
            {
                active = _pkActive,
                opponent = _pkOpponent != null ? (JsonNode?)_pkOpponent.DeepClone() : null,
                since = _pkSince,
                score = 0L,
                votes = _pkVotes,
                title = _pkTitle,
                cmd = _pkCmd,
            };
    }

    private async Task RefreshPkStatsAsync()
    {
        string? oppRoom;
        lock (_pkLock)
        {
            if (!_pkActive || _pkOpponent == null) return;
            oppRoom = _pkOpponent["roomId"]?.GetValue<string>();
        }
        if (string.IsNullOrEmpty(oppRoom)) return;
        try
        {
            var st = await BiliApi.GetRoomStatsAsync(long.Parse(oppRoom), _config.Cookie, _cts.Token);
            lock (_pkLock)
            {
                if (_pkOpponent == null) return;
                _pkOpponent["online"] = st.Online;
                _pkOpponent["popularity"] = st.Popularity;
                _pkOpponent["follower"] = st.Follower;
                _pkOpponent["guard"] = st.Guard;
                _pkOpponent["uname"] = st.Uname;
                if (st.Title.Length > 0) _pkTitle = st.Title;
            }
            _hub.PublishOutbound("pkinfo", PkStatePayload());
        }
        catch { }
    }

    private async Task HandlePkEventAsync(LiveEvent ev)
    {
        var pkEnabled = (_config.GetNode("pk") as JsonObject)?.TryGetPropertyValue("enabled", out var en) == true
                        && en is JsonValue pv && pv.TryGetValue<bool>(out var pb) && pb;
        if (!pkEnabled) return;
        var cmd = ev.Cmd ?? "";
        lock (_pkLock) _pkCmd = cmd;
        if (Regex.IsMatch(cmd, "SETTLE|RESULT|END|AGAIN"))
        {
            lock (_pkLock)
            {
                _pkActive = false;
                _pkOpponent = null;
                _pkTitle = "";
            }
            _pkLoopCts?.Cancel();
            _hub.PublishOutbound("pkinfo", PkStatePayload());
            return;
        }
        var myRoom = _hub.LastStatus.RealRoomId;
        var oppRoom = ExtractRoom(ev.Data, myRoom);
        var votes = ExtractVotes(ev.Data);
        lock (_pkLock)
        {
            if (votes > _pkVotes) _pkVotes = votes;
            _pkActive = true;
            if (_pkSince == 0) _pkSince = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (oppRoom != null && (_pkOpponent == null || _pkOpponent["roomId"]?.GetValue<string>() != oppRoom))
                _pkOpponent = new JsonObject { ["roomId"] = oppRoom };
        }
        if (oppRoom != null)
            await RefreshPkStatsAsync();
        else
            _hub.PublishOutbound("pkinfo", PkStatePayload());
        EnsurePkLoop();
    }

    private void EnsurePkLoop()
    {
        lock (_pkLock)
        {
            if (_pkLoopCts != null && !_pkLoopCts.IsCancellationRequested) return;
            _pkLoopCts = new CancellationTokenSource();
        }
        var ct = _pkLoopCts.Token;
        var refreshMs = ((_config.GetNode("pk") as JsonObject)?.TryGetPropertyValue("refreshMs", out var rm) == true && rm is JsonValue rv)
            ? rv.GetValue<long?>() ?? 8000 : 8000;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(2000, refreshMs)), ct);
                    await RefreshPkStatsAsync();
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    public async Task<object> SetPkTargetAsync(string roomId)
    {
        lock (_pkLock)
        {
            _pkActive = true;
            _pkSince = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            _pkOpponent = new JsonObject { ["roomId"] = roomId };
        }
        await RefreshPkStatsAsync();
        EnsurePkLoop();
        return PkStatePayload();
    }

    public object PkStatus()
    {
        var pk = _config.GetNode("pk") as JsonObject ?? new JsonObject();
        bool Flag(string key) => pk.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<bool>(out var b) && b;
        var payload = (JsonObject)JsonNode.Parse(JsonSerializerOf(PkStatePayload()))!;
        payload["enabled"] = Flag("enabled");
        payload["showOnFloating"] = Flag("showOnFloating");
        return payload;
    }

    private static string JsonSerializerOf(object o)
        => System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    // ---------------- auto like ----------------

    public object LikeStatus()
    {
        lock (_likeLock)
            return new { running = _likeRunning, liked = _likeLiked, error = _likeError, stop = _likeStop };
    }

    public object LikeStart()
    {
        // Require a real connection: falling back to the configured room id let the
        // loop hammer the like API for a room that was never connected (that is how
        // the earlier "点赞频率限制(-352)" came about, with 0 likes reported).
        var ridStr = _hub.LastStatus.RealRoomId;
        if (string.IsNullOrEmpty(ridStr) || ridStr == "0")
            return new { error = "未连接直播间：请先在「直播间连接」连上开播中的房间" };
        if (string.IsNullOrEmpty(_config.Cookie)) return new { error = "未配置 Cookie" };
        lock (_likeLock)
        {
            if (_likeRunning) return new { error = "点赞正在进行中" };
            _likeRunning = true;
            _likeLiked = 0;
            _likeError = "";
            _likeStop = false;
        }
        var roomId = long.Parse(ridStr);
        _ = Task.Run(async () =>
        {
            var retry352 = 0;
            try
            {
                var ctx = await BiliApi.InitLikeContextAsync(roomId, _config.Cookie, _cts.Token);
                if (ctx.Uid == 0) throw new Exception("无法获取用户uid，请检查Cookie是否有效");
                if (ctx.AnchorId == 0) throw new Exception("无法获取主播uid");
                // Liking an offline room is what trips B站 risk control (-352); tell the
                // user instead of retrying into a wall.
                bool roomLive;
                try
                {
                    var st = await BiliApi.GetRoomLiveStatusAsync(roomId, _config.Cookie, _cts.Token);
                    roomLive = st.LiveStatus == 1;
                }
                catch (OperationCanceledException) { throw; }
                catch { roomLive = true; }   // probe failed: let the like call report its own error
                if (!roomLive) throw new Exception("房间未开播：点赞需要直播间在线");
                while (true)
                {
                    bool stop;
                    lock (_likeLock) stop = _likeStop;
                    if (stop) break;
                    var (code, message) = await BiliApi.LikeOnceAsync(ctx, _cts.Token);
                    if (code == 0)
                    {
                        lock (_likeLock) { _likeLiked++; _likeError = ""; }
                        retry352 = 0;
                    }
                    else if (code == -352 && retry352 < 3)
                    {
                        // B站风控(-352)：连续快速点击会触发，越急越容易加深限制。
                        // 退避 5s → 15s → 30s，仍失败就停，不要继续探。
                        retry352++;
                        var wait = retry352 switch { 1 => 5, 2 => 15, _ => 30 };
                        lock (_likeLock)
                            _likeError = $"B站风控限制了点赞(-352)，{wait} 秒后重试（{retry352}/3）";
                        await Task.Delay(wait * 1000, _cts.Token);
                        continue;
                    }
                    else
                    {
                        lock (_likeLock)
                            _likeError = (message.Length > 0 ? message : "code:" + code) +
                                (code == -352 ? "：多为 B站风控（该接口对同一账号有节奏限制），稍后重试或降低频率" : "");
                        break;
                    }
                    await Task.Delay(300, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                lock (_likeLock) _likeError = e.Message;
            }
            lock (_likeLock) _likeRunning = false;
        });
        return new { ok = true };
    }

    public object LikeStop()
    {
        lock (_likeLock) _likeStop = true;
        return new { ok = true };
    }

    // ---------------- multi-room monitor ----------------

    private JsonArray MonitoredRooms()
        => (_config.GetNode("monitoredRooms") as JsonArray)?.DeepClone()!.AsArray() ?? new JsonArray();

    private object MonitorPayload(bool ok)
    {
        var rooms = new JsonArray();
        foreach (var r in MonitoredRooms())
        {
            if (r is not JsonObject o) continue;
            var clone = (JsonObject)o.DeepClone()!;
            var rid = o["roomId"]?.GetValue<string>() ?? "";
            lock (_monitorState)
                clone["state"] = _monitorState.TryGetValue(rid, out var st) ? st.DeepClone() : null;
            rooms.Add(clone);
        }
        var iv = 30L;
        if (_config.GetNode("monitorIntervalSec") is JsonValue ivn) iv = ivn.TryGetValue<long>(out var ivv) ? ivv : 30;
        return new { ok, intervalSec = Math.Max(15, iv), rooms };
    }

    public object MonitorGet() => MonitorPayload(true);

    public async Task<object> MonitorPostAsync(JsonObject body)
    {
        var action = body["action"]?.GetValue<string>() ?? "";
        var rid = (body["roomId"]?.GetValue<string>() ?? "").Trim();
        var doc = _config.Snapshot();
        var rooms = (doc["monitoredRooms"] as JsonArray) ?? new JsonArray();
        doc["monitoredRooms"] = rooms;

        switch (action)
        {
            case "add":
            {
                if (!Regex.IsMatch(rid, "^\\d+$")) return new { error = "房间号必须是数字" };
                if (rooms.Any(r => (r as JsonObject)?["roomId"]?.GetValue<string>() == rid))
                    return new { error = "该房间已在监控列表" };
                if (rooms.Count >= 8) return new { error = "最多监控 8 个房间" };
                rooms.Add(new JsonObject { ["roomId"] = rid, ["enabled"] = true });
                _config.ReplaceFrom(doc);
                try
                {
                    var st = await BiliApi.GetRoomStatsAsync(long.Parse(rid), _config.Cookie, _cts.Token);
                    lock (_monitorState) _monitorState[rid] = StatsToNode(st);
                }
                catch { }
                _hub.PublishOutbound("monitor", MonitorPayload(true));
                return MonitorPayload(true);
            }
            case "remove":
            {
                for (var i = rooms.Count - 1; i >= 0; i--)
                    if ((rooms[i] as JsonObject)?["roomId"]?.GetValue<string>() == rid)
                        rooms.RemoveAt(i);
                lock (_monitorState) _monitorState.Remove(rid);
                _config.ReplaceFrom(doc);
                _hub.PublishOutbound("monitor", MonitorPayload(true));
                return MonitorPayload(true);
            }
            case "toggle":
            {
                var found = rooms.FirstOrDefault(r => (r as JsonObject)?["roomId"]?.GetValue<string>() == rid) as JsonObject;
                if (found == null) return new { error = "房间不在监控列表" };
                found["enabled"] = body["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var b) && b;
                _config.ReplaceFrom(doc);
                _hub.PublishOutbound("monitor", MonitorPayload(true));
                return new { ok = true };
            }
            case "interval":
            {
                var iv = body["intervalSec"] is JsonValue ivn && ivn.TryGetValue<long>(out var ivv) ? ivv : 30;
                doc["monitorIntervalSec"] = Math.Clamp(iv, 15, 120);
                _config.ReplaceFrom(doc);
                _hub.PublishOutbound("monitor", MonitorPayload(true));
                return new { ok = true, intervalSec = Math.Clamp(iv, 15, 120) };
            }
            default:
                return new { error = "未知操作" };
        }
    }

    private static JsonObject StatsToNode(BiliApi.RoomStats st) => new()
    {
        ["roomId"] = st.RoomId, ["online"] = st.Online, ["popularity"] = st.Popularity,
        ["follower"] = st.Follower, ["guard"] = st.Guard, ["title"] = st.Title, ["uname"] = st.Uname,
        ["checkedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        ["lastOk"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        ["lastErr"] = "",
    };

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                var rooms = MonitoredRooms();
                var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                if (now - _monitorLastFetchAt < 2000) continue;
                var iv = Math.Max(15, _config.GetNode("monitorIntervalSec") is JsonValue ivn && ivn.TryGetValue<long>(out var ivv) ? ivv : 30) * 1000;
                string? due = null;
                foreach (var r in rooms)
                {
                    if (r is not JsonObject o) continue;
                    var rid = o["roomId"]?.GetValue<string>() ?? "";
                    var enabled = o["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var b) ? b : true;
                    if (!enabled || !Regex.IsMatch(rid, "^\\d+$")) continue;
                    lock (_monitorState)
                    {
                        if (!_monitorState.TryGetValue(rid, out var st)) { due = rid; break; }
                        var checkedAt = st["checkedAt"]?.GetValue<long?>() ?? 0;
                        var checking = st["checking"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) && cb;
                        if (!checking && now - checkedAt >= iv) { due = rid; break; }
                    }
                }
                if (due == null) continue;
                _monitorLastFetchAt = now;
                var dueRoom = due;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var st = await BiliApi.GetRoomStatsAsync(long.Parse(dueRoom), _config.Cookie, ct);
                        lock (_monitorState) _monitorState[dueRoom] = StatsToNode(st);
                        _hub.PublishOutbound("monitor", MonitorPayload(true));
                    }
                    catch
                    {
                        lock (_monitorState)
                        {
                            var st = _monitorState.TryGetValue(dueRoom, out var old) ? old : new JsonObject();
                            st["lastErr"] = "查询失败";
                            st["checkedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        }
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _hub.OnEvent -= HandleEvent;
        _aggregator.OnMerged -= PublishMerged;
        _cts.Cancel();
        _pkLoopCts?.Cancel();
    }
}
