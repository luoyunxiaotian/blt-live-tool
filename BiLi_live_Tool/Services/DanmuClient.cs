using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BiLi_live_Tool.Services;

internal enum CloseReason { Dropped, Stale, ConnectFail, Exhausted, Cancelled }

internal readonly record struct Packet(uint Op, ushort Protover, ReadOnlyMemory<byte> Body);

/// <summary>
/// Accumulates bytes and yields complete B站 protocol packets (16-byte header),
/// tolerant of packets fragmented across WS messages.
/// </summary>
internal sealed class PacketBuffer
{
    private byte[] _data = new byte[64 * 1024];
    private int _len;

    public void Append(byte[] chunk, int count)
    {
        if (_len + count > _data.Length) Array.Resize(ref _data, Math.Max(_data.Length * 2, _len + count));
        Buffer.BlockCopy(chunk, 0, _data, _len, count);
        _len += count;
    }

    public IEnumerable<Packet> DrainPackets()
    {
        int offset = 0;
        while (offset + 16 <= _len)
        {
            uint totalLen = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4));
            if (totalLen < 16 || offset + (long)totalLen > _len) break;
            ushort headerLen = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset + 4, 2));
            if (headerLen < 16 || headerLen > totalLen) break;
            ushort protover = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset + 6, 2));
            uint op = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset + 8, 4));
            int bodyLen = (int)totalLen - headerLen;
            var body = bodyLen > 0 ? _data.AsMemory(offset + headerLen, bodyLen) : ReadOnlyMemory<byte>.Empty;
            yield return new Packet(op, protover, body);
            offset += (int)totalLen;
        }
        if (offset > 0)
        {
            Buffer.BlockCopy(_data, offset, _data, 0, _len - offset);
            _len -= offset;
        }
    }

    public static List<Packet> ParseAll(ReadOnlySpan<byte> bytes)
    {
        var list = new List<Packet>();
        int offset = 0;
        while (offset + 16 <= bytes.Length)
        {
            uint totalLen = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (totalLen < 16 || offset + (long)totalLen > bytes.Length) break;
            ushort headerLen = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 4, 2));
            if (headerLen < 16 || headerLen > totalLen) break;
            ushort protover = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 6, 2));
            uint op = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8, 4));
            int bodyLen = (int)totalLen - headerLen;
            var body = bodyLen > 0 ? bytes.Slice(offset + headerLen, bodyLen).ToArray() : ReadOnlySpan<byte>.Empty.ToArray();
            list.Add(new Packet(op, protover, body));
            offset += (int)totalLen;
        }
        return list;
    }
}

/// <summary>
/// Port of connect() in Bin/lib/bili.js (with the RFC6455 client of lib/ws.js
/// replaced by System.Net.WebSockets.ClientWebSocket).
/// Flow: resolve room → check live (poll if not live) → danmu info → WS with
/// host rotation + auth + heartbeat + stale watchdog + full-flow reconnect.
/// </summary>
public sealed class DanmuClient
{
    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
    private static readonly Regex DedeUserIdRegex = new("(?:^|;\\s*)DedeUserID=(\\d+)", RegexOptions.Compiled);

    private readonly EventHub _hub;
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;

    // Raw-frame capture, bucketed per cmd: a busy room floods a flat ring
    // buffer with DANMU_MSG, so each cmd keeps its own small history and rare
    // frames (gifts!) survive long enough to be inspected.
    private readonly Dictionary<string, Queue<string>> _rawByCmd = new();
    private readonly object _rawLock = new();
    private const int RawPerCmd = 4;

    public DanmuClient(EventHub hub) { _hub = hub; }

    /// <summary>Recent raw frames as "CMD :: {json}", grouped by cmd. filter = cmd prefix.</summary>
    public List<string> RecentRawFrames(string? filter = null, int max = 60)
    {
        lock (_rawLock)
        {
            var all = new List<string>();
            foreach (var kv in _rawByCmd)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    !kv.Key.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) continue;
                all.AddRange(kv.Value);
            }
            return all.Count <= max ? all : all.GetRange(all.Count - max, max);
        }
    }

    /// <summary>Cmd strings seen so far with their captured counts (diagnostics).</summary>
    public Dictionary<string, int> SeenCmds()
    {
        lock (_rawLock) return _rawByCmd.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
    }

    private void CaptureFrame(string cmd, JsonElement msg)
    {
        try
        {
            var key = cmd.Split(':')[0].Trim();   // SEND_GIFT:xxx → SEND_GIFT
            var raw = msg.GetRawText();
            if (raw.Length > 6000) raw = raw.Substring(0, 6000) + "…[truncated]";
            lock (_rawLock)
            {
                if (!_rawByCmd.TryGetValue(key, out var q))
                    _rawByCmd[key] = q = new Queue<string>();
                q.Enqueue(key + " :: " + raw);
                while (q.Count > RawPerCmd) q.Dequeue();
            }
        }
        catch { }
    }

    public void Start(string roomId, string cookie)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => RunAsync(roomId, cookie, ct), CancellationToken.None);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        _cts = null;
        _ws = null;
    }

    private void Status(string state, string realRoomId = "", long popularity = -1, string error = "", string title = "", string uid = "")
    {
        var cur = _hub.LastStatus;
        var nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var connectedAt = state == "connected"
            ? (cur.State == "connected" && cur.ConnectedAt > 0 ? cur.ConnectedAt : nowMs)
            : 0;
        _hub.PublishStatus(new LiveStatusInfo
        {
            State = state,
            RealRoomId = realRoomId.Length > 0 ? realRoomId : cur.RealRoomId,
            AnchorUid = uid.Length > 0 ? uid : (state == "connected" ? cur.AnchorUid : ""),
            Popularity = popularity >= 0 ? popularity : cur.Popularity,
            ConnectedAt = connectedAt,
            Error = error,
            Title = title.Length > 0 ? title : cur.Title,
        });
    }

    private async Task RunAsync(string roomId, string cookie, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Status("resolving");
                var room = await BiliApi.GetRealRoomIdAsync(roomId, ct);
                if (ct.IsCancellationRequested) return;
                Status("checking_live", room.RealRoomId.ToString(), uid: room.Uid.ToString());
                var live = await BiliApi.GetRoomLiveStatusAsync(room.RealRoomId, cookie, ct);
                if (ct.IsCancellationRequested) return;

                if (live.LiveStatus != 1)
                {
                    // Not live yet: poll every 10s and auto-connect once live (same as bili.js pollTimer).
                    Status("not_live", room.RealRoomId.ToString(), title: live.Title);
                    var becameLive = false;
                    while (!ct.IsCancellationRequested && !becameLive)
                    {
                        try { await Task.Delay(10000, ct); } catch (OperationCanceledException) { return; }
                        try
                        {
                            var l = await BiliApi.GetRoomLiveStatusAsync(room.RealRoomId, cookie, ct);
                            if (l.LiveStatus == 1) becameLive = true;
                        }
                        catch { /* keep polling through transient errors */ }
                    }
                    if (!becameLive) return;
                    Status("getting_danmu");
                }
                else
                {
                    Status("getting_danmu");
                }

                var info = await BiliApi.GetDanmuInfoAsync(room.RealRoomId, cookie, ct);
                if (ct.IsCancellationRequested) return;
                var reason = await OpenWsAsync(room.RealRoomId, room.Uid, info, cookie, ct);
                if (reason == CloseReason.Cancelled) return;
                // Dropped mid-session or all hosts exhausted → restart full flow after 3s.
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Hard error stops retrying (same as bili.js); the user clicks connect again.
                if (!ct.IsCancellationRequested) Status("error", error: ex.Message);
                return;
            }
        }
    }

    private async Task<CloseReason> OpenWsAsync(long realRoomId, long uid, DanmuInfo info, string cookie, CancellationToken ct)
    {
        if (info.Hosts.Count == 0)
        {
            Status("error", error: "没有可用弹幕服务器");
            return CloseReason.Exhausted;
        }
        for (int idx = 0; idx < info.Hosts.Count; idx++)
        {
            if (ct.IsCancellationRequested) return CloseReason.Cancelled;
            var reason = await TryHostAsync(realRoomId, uid, info, info.Hosts[idx], cookie, ct);
            if (reason != CloseReason.Stale && reason != CloseReason.ConnectFail) return reason;
            // Stale / connect failure → rotate to next host.
        }
        return CloseReason.Exhausted;
    }

    private async Task<CloseReason> TryHostAsync(long realRoomId, long uid, DanmuInfo info, DanmuHost host, string cookie, CancellationToken ct)
    {
        var port = host.WssPort > 0 ? host.WssPort : (host.Port > 0 ? host.Port : 443);
        var url = $"wss://{host.Host}:{port}/sub";
        var ws = new ClientWebSocket();
        // B站 servers don't need protocol-level pings; bili.js only sends app-level heartbeats.
        ws.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        ws.Options.SetRequestHeader("User-Agent", Ua);
        ws.Options.SetRequestHeader("Origin", "https://live.bilibili.com");
        ws.Options.SetRequestHeader("Referer", "https://live.bilibili.com/");
        if (!string.IsNullOrEmpty(cookie)) ws.Options.SetRequestHeader("Cookie", cookie);
        _ws = ws;

        var opened = false;
        var stale = false;
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                await ws.ConnectAsync(new Uri(url), connectCts.Token);
            }
            opened = true;

            // Auth: use DedeUserID from cookie as uid when present, else anonymous 0.
            var authUid = 0;
            var m = DedeUserIdRegex.Match(cookie ?? "");
            if (m.Success) int.TryParse(m.Groups[1].Value, out authUid);
            var auth = JsonSerializer.Serialize(new { uid = authUid, roomid = realRoomId, protover = 3, platform = "web", type = 2, key = info.Token });
            var authBytes = BuildPacket(7, Encoding.UTF8.GetBytes(auth));
            await ws.SendAsync(authBytes, WebSocketMessageType.Binary, true, ct);
            Status("connected", realRoomId.ToString(), uid: uid.ToString());

            var lastDataTicks = Environment.TickCount64;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var heartbeat = Task.Run(async () =>
            {
                var hb = BuildPacket(2, Encoding.UTF8.GetBytes("[object Object]"));
                while (!linked.IsCancellationRequested)
                {
                    try { await Task.Delay(30000, linked.Token); } catch (OperationCanceledException) { return; }
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            await ws.SendAsync(hb, WebSocketMessageType.Binary, true, linked.Token);
                    }
                    catch { return; }
                }
            }, CancellationToken.None);

            var watchdog = Task.Run(async () =>
            {
                while (!linked.IsCancellationRequested)
                {
                    try { await Task.Delay(10000, linked.Token); } catch (OperationCanceledException) { return; }
                    if (Environment.TickCount64 - lastDataTicks > 45000)
                    {
                        stale = true;
                        try { ws.Abort(); } catch { }
                        return;
                    }
                }
            }, CancellationToken.None);

            var buf = new PacketBuffer();
            var chunk = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult res;
                try { res = await ws.ReceiveAsync(new ArraySegment<byte>(chunk), ct); }
                catch { break; }
                if (res.MessageType == WebSocketMessageType.Close) break;
                if (res.Count > 0)
                {
                    lastDataTicks = Environment.TickCount64;
                    buf.Append(chunk, res.Count);
                    foreach (var pkt in buf.DrainPackets())
                        HandlePacket(pkt, realRoomId);
                }
            }

            linked.Cancel();
            try { await heartbeat; } catch { }
            try { await watchdog; } catch { }

            if (ct.IsCancellationRequested) return CloseReason.Cancelled;
            if (stale)
            {
                Status("reconnecting", realRoomId.ToString(), error: "数据停滞，正在重连");
                return CloseReason.Stale;
            }
            return opened ? CloseReason.Dropped : CloseReason.ConnectFail;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CloseReason.Cancelled;
        }
        catch
        {
            return opened ? CloseReason.Dropped : CloseReason.ConnectFail;
        }
        finally
        {
            try { ws.Dispose(); } catch { }
            if (ReferenceEquals(_ws, ws)) _ws = null;
        }
    }

    internal static byte[] BuildPacket(int op, byte[] body, ushort protover = 1)
    {
        var buf = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)(16 + body.Length));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4, 2), 16);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(6, 2), protover);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), (uint)op);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), 1);
        Buffer.BlockCopy(body, 0, buf, 16, body.Length);
        return buf;
    }

    private void HandlePacket(Packet pkt, long realRoomId)
    {
        if (pkt.Op == 3)
        {
            // Heartbeat reply (op=3): popularity value (deprecated by B站, kept for parity).
            var pop = pkt.Body.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(pkt.Body.Span) : 0;
            Status("connected", realRoomId.ToString(), popularity: pop);
            return;
        }
        if (pkt.Op != 5) return;

        if (pkt.Protover == 2 || pkt.Protover == 3)
        {
            byte[] inflated;
            try { inflated = Decompress(pkt.Body.Span, pkt.Protover == 3); }
            catch { return; }
            foreach (var inner in PacketBuffer.ParseAll(inflated))
                HandlePacket(inner, realRoomId);
            return;
        }
        if (pkt.Protover != 0) return;

        try
        {
            using var doc = JsonDocument.Parse(pkt.Body);
            HandleJson(doc.RootElement, realRoomId);
        }
        catch { /* ignore bad packets, same as bili.js */ }
    }

    private static byte[] Decompress(ReadOnlySpan<byte> data, bool brotli)
    {
        var tmp = data.ToArray();
        using var src = new MemoryStream(tmp);
        using Stream ds = brotli
            ? new BrotliStream(src, CompressionMode.Decompress)
            : new ZLibStream(src, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        ds.CopyTo(outMs);
        return outMs.ToArray();
    }

    private void HandleJson(JsonElement msg, long realRoomId)
    {
        var cmd = msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? ""
            : "";
        CaptureFrame(cmd, msg);

        if (cmd.StartsWith("ONLINE_RANK_COUNT", StringComparison.Ordinal))
        {
            long count = 0;
            if (msg.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                count = d.TryGetProperty("count", out var cd) && cd.ValueKind == JsonValueKind.Number ? cd.GetInt64() : 0;
            Status("connected", realRoomId.ToString(), popularity: count);
            return;
        }

        var ev = BiliNormalize.Normalize(cmd, msg);
        if (ev != null) _hub.Publish(ev);
    }
}

/// <summary>Port of normalize() and its helpers (deepFindUser/detectGuard/extractUserTier) in bili.js, demo event subset.</summary>
public static class BiliNormalize
{
    private static readonly string[] GuardNames = { "", "总督", "提督", "舰长" };

    public static LiveEvent? Normalize(string cmd, JsonElement msg)
    {
        try
        {
            var now = DateTime.Now;
            string time = now.ToString("yyyy-MM-dd HH:mm:ss");
            long ts = new DateTimeOffset(now, TimeZoneInfo.Local.GetUtcOffset(now)).ToUnixTimeMilliseconds();

            if (cmd.StartsWith("DANMU_MSG", StringComparison.Ordinal))
            {
                if (!msg.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array) return null;
                var text = info.GetArrayLength() > 1 && info[1].ValueKind == JsonValueKind.String ? info[1].GetString() ?? "" : "";
                var uid = "";
                var uname = "";
                var uface = "";
                if (info.GetArrayLength() > 2 && info[2].ValueKind == JsonValueKind.Array)
                {
                    var u = info[2];
                    if (u.GetArrayLength() > 0) uid = Plain(u[0]);
                    if (u.GetArrayLength() > 1 && u[1].ValueKind == JsonValueKind.String) uname = u[1].GetString() ?? "";
                }
                if (info.GetArrayLength() > 3 && info[3].ValueKind == JsonValueKind.Array && info[3].GetArrayLength() > 0
                    && info[3][0].ValueKind == JsonValueKind.String)
                    uface = info[3][0].GetString() ?? "";
                var tier = ExtractTier(msg);
                var guard = DetectGuard(msg);
                return new LiveEvent
                {
                    Type = "danmu", Time = time, Ts = ts,
                    Uid = uid, Uname = uname, Msg = text, Uface = uface,
                    MedalLevel = tier.Medal, HonorLevel = tier.Honor,
                    IsGuard = guard.IsGuard, GuardLevel = guard.Level,
                };
            }

            if (cmd.StartsWith("SEND_GIFT", StringComparison.Ordinal))
            {
                if (!msg.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;

                // SEND_GIFT_V2 (current server protocol) carries the gift as a
                // base64 protobuf blob in data.pb — the JSON fields below simply
                // do not exist there, which is why every V2 gift used to land as
                // an empty "GIFT — 送出 ×1" row with no name to thank for.
                if (GetProp(d, "pb") is { ValueKind: JsonValueKind.String } pbEl)
                {
                    try
                    {
                        var g = ParseGiftPb(Convert.FromBase64String(pbEl.GetString() ?? ""));
                        if (g.Uname.Length > 0 || g.GiftName.Length > 0)
                        {
                            var perYuanPb = g.CoinType == "silver" ? 10000 : 1000;
                            return new LiveEvent
                            {
                                Type = "gifts", Time = time, Ts = ts,
                                Uid = g.Uid, Uname = g.Uname,
                                GiftName = g.GiftName, Num = g.Num, Price = g.Price,
                                TotalCoin = g.TotalCoin, CoinType = g.CoinType,
                                Value = Math.Round(g.TotalCoin / (double)perYuanPb * 100) / 100,
                                MedalLevel = g.Medal,
                            };
                        }
                    }
                    catch { }
                }

                long num = GetLong(d, "num", 1); if (num < 1) num = 1;
                long price = GetLong(d, "price");
                long totalCoin = GetLong(d, "total_coin"); if (totalCoin == 0) totalCoin = num * price;
                var coinType = GetStr(d, "coin_type"); if (coinType.Length == 0) coinType = "gold";
                double perYuan = coinType == "gold" ? 1000 : 10000;
                var value = Math.Round(totalCoin / perYuan * 100) / 100;
                var tier = ExtractTier(msg);
                var guard = DetectGuard(msg);
                // Field-name drift across gift variants (plain / blind box / combo):
                // accept the snake_case aliases and the blind box's inner gift so a
                // frame we cannot fully map still lands with its real name.
                var giftName = GetStr(d, "giftName");
                if (giftName.Length == 0) giftName = GetStr(d, "gift_name");
                if (GetProp(d, "blind_gift") is { ValueKind: JsonValueKind.Object } blind)
                {
                    if (giftName.Length == 0) giftName = GetStr(blind, "gift_name");
                    if (price == 0) price = GetLong(blind, "gift_price", price);
                }
                var uname = GetStr(d, "uname");
                if (uname.Length == 0) uname = GetStr(d, "user_name");
                if (uname.Length == 0 && GetProp(d, "user_info") is { ValueKind: JsonValueKind.Object } ui)
                    uname = GetStr(ui, "uname");
                // Never emit a nameless, meaningless row (that is what showed up as
                // “GIFT — 送出 ×1” in the stream and blocked the thank-you danmu).
                if (uname.Length == 0 && giftName.Length == 0) return null;
                return new LiveEvent
                {
                    Type = "gifts", Time = time, Ts = ts,
                    Uid = Plain(GetProp(d, "uid")), Uname = uname,
                    GiftName = giftName, Num = (int)num, Price = price,
                    TotalCoin = totalCoin, CoinType = coinType, Value = value,
                    MedalLevel = tier.Medal, HonorLevel = tier.Honor,
                    IsGuard = guard.IsGuard, GuardLevel = guard.Level,
                };
            }

            if (cmd.StartsWith("GUARD_BUY", StringComparison.Ordinal))
            {
                if (!msg.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;
                long level = GetLong(d, "guard_level", 3); if (level < 1) level = 3;
                long num = GetLong(d, "num", 1); if (num < 1) num = 1;
                long price = GetLong(d, "price");
                var value = Math.Round(num * price / 1000.0 * 100) / 100;
                return new LiveEvent
                {
                    Type = "guard", Time = time, Ts = ts,
                    Uid = Plain(GetProp(d, "uid")), Uname = GetStr(d, "uname"),
                    Level = (int)level,
                    LevelName = level >= 1 && level <= 3 ? GuardNames[level] : "等级" + level,
                    Num = (int)num, Price = price, Value = value,
                    IsGuard = true, GuardLevel = (int)level,
                };
            }

            if (cmd.StartsWith("SUPER_CHAT_MESSAGE", StringComparison.Ordinal))
            {
                if (!msg.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;
                var user = GetProp(d, "user");
                var tier = ExtractTier(msg);
                var guard = DetectGuard(msg);
                return new LiveEvent
                {
                    Type = "superchat", Time = time, Ts = ts,
                    Uid = Plain(GetProp(user, "uid")), Uname = GetStr(user, "uname"),
                    Msg = GetStr(d, "message"), Price = GetLong(d, "price"), Uface = GetStr(user, "face"),
                    StartTime = GetLong(d, "start_time"), EndTime = GetLong(d, "end_time"),
                    MedalLevel = tier.Medal, HonorLevel = tier.Honor,
                    IsGuard = guard.IsGuard, GuardLevel = guard.Level,
                };
            }

            // 观众进房 (msg_type=1) / 关注 (msg_type=2) / 进场特效 (舰长等)
            if (cmd.StartsWith("INTERACT_WORD", StringComparison.Ordinal) || cmd.StartsWith("ENTRY_EFFECT", StringComparison.Ordinal))
            {
                if (!msg.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;
                var fromDeep = DeepFindUser(msg);
                var pbUid = "";
                var pbUname = "";
                if (GetProp(d, "pb") is var pbEl && pbEl.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        var r = ParseInteractPb(Convert.FromBase64String(pbEl.GetString() ?? ""));
                        pbUid = r.Uid;
                        pbUname = r.Uname;
                    }
                    catch { }
                }
                var g = DetectGuard(d);
                var tier = ExtractTier(d);
                var msgType = GetLong(d, "msg_type") != 0 ? GetLong(d, "msg_type") : GetLong(msg, "msg_type");
                var uid = GetProp(d, "uid") switch
                {
                    { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
                    { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
                    _ => pbUid.Length > 0 ? pbUid : fromDeep.Uid,
                };
                var uname = FirstNonEmpty(GetStr(d, "uname"), pbUname, fromDeep.Uname);
                return new LiveEvent
                {
                    Type = "interact", Time = time, Ts = ts,
                    Uid = uid, Uname = uname, MsgType = (int)msgType,
                    IsGuard = g.IsGuard, GuardLevel = g.Level,
                    MedalLevel = tier.Medal, HonorLevel = tier.Honor,
                };
            }

            // PK 事件（原始 cmd + data 交给 LivePipeline 的状态机）
            if (cmd.StartsWith("PK_BATTLE", StringComparison.Ordinal) || cmd.StartsWith("PK_SETTLE", StringComparison.Ordinal)
                || cmd.StartsWith("PK_AGAIN", StringComparison.Ordinal) || cmd.StartsWith("PK_RESULT", StringComparison.Ordinal))
            {
                JsonNode? data = null;
                if (msg.TryGetProperty("data", out var dEl))
                {
                    try { data = JsonNode.Parse(dEl.GetRawText()); } catch { }
                }
                return new LiveEvent { Type = "pk", Time = time, Ts = ts, Cmd = cmd, Data = data ?? new JsonObject() };
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    // Port of deepFindUser: first node carrying uname/uid anywhere in the payload.
    private static (string Uname, string Uid) DeepFindUser(JsonElement root)
    {
        var found = ("", "");
        Walk(root);
        return found;

        void Walk(JsonElement el)
        {
            if (found.Item1.Length > 0 || found.Item2.Length > 0) return;
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var u = "";
                    var id = "";
                    foreach (var p in el.EnumerateObject())
                    {
                        if (p.Name == "uname" && p.Value.ValueKind == JsonValueKind.String) u = p.Value.GetString() ?? "";
                        if (p.Name == "uid")
                        {
                            if (p.Value.ValueKind == JsonValueKind.Number) id = p.Value.GetRawText();
                            else if (p.Value.ValueKind == JsonValueKind.String) id = p.Value.GetString() ?? "";
                        }
                    }
                    if (u.Length > 0 || id.Length > 0) { found = (u, id); return; }
                    foreach (var p in el.EnumerateObject()) Walk(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray()) Walk(item);
                    break;
            }
        }
    }

    // Port of parseInteractPb: INTERACT_WORD_V2 carries the user inside base64 protobuf.
    private static (string Uid, string Uname) ParseInteractPb(byte[] buf)    {
        var uid = "";
        var uname = "";
        var pos = 0;
        ulong ReadVarint()
        {
            ulong v = 0;
            var shift = 0;
            while (pos < buf.Length)
            {
                var x = buf[pos++];
                v |= (ulong)(x & 0x7f) << shift;
                if ((x & 0x80) == 0) break;
                shift += 7;
            }
            return v;
        }
        try
        {
            while (pos < buf.Length)
            {
                var tag = ReadVarint();
                var field = tag >> 3;
                var wire = tag & 7;
                if (wire == 0)
                {
                    var v = ReadVarint();
                    if (field == 1) uid = v.ToString();
                }
                else if (wire == 2)
                {
                    var len = (int)ReadVarint();
                    if (len < 0 || pos + len > buf.Length) break;
                    var s = Encoding.UTF8.GetString(buf, pos, len);
                    pos += len;
                    if (field == 2 && s.Trim().Length > 0) uname = s;
                }
                else if (wire == 1) pos += 8;
                else if (wire == 5) pos += 4;
                else break;
            }
        }
        catch { }
        return (uid, uname);
    }

    /// <summary>
    /// SEND_GIFT_V2 protobuf (field numbers verified against live frames
    /// 2026-09-13): f1 uid, f2 uname, f8.f5 medal level, f10 = gift message
    /// {f2 name, f5 price, f8 coin_type, f14 total_coin, f3/f17 count}.
    /// The count is derived from total/price when possible so it is right
    /// whichever count field the server happens to fill.
    /// </summary>
    private static (string Uid, string Uname, string GiftName, int Num, long Price, long TotalCoin, string CoinType, int Medal)
        ParseGiftPb(byte[] buf)
    {
        var uid = "";
        var uname = "";
        var giftName = "";
        var coinType = "gold";
        long price = 0, totalCoin = 0, numA = 0, numB = 0;
        var medal = 0;
        var pos = 0;

        ulong ReadVarint()
        {
            ulong v = 0; var shift = 0;
            while (pos < buf.Length)
            {
                var c = buf[pos++];
                v |= (ulong)(c & 0x7f) << shift;
                if ((c & 0x80) == 0) break;
                shift += 7;
            }
            return v;
        }

        byte[] ReadBytes(int len)
        {
            if (len <= 0 || pos + len > buf.Length) { pos = buf.Length; return Array.Empty<byte>(); }
            var b = new byte[len];
            Buffer.BlockCopy(buf, pos, b, 0, len);
            pos += len;
            return b;
        }

        void ParseGiftMessage(byte[] g)
        {
            var i = 0;
            ulong Rv()
            {
                ulong v = 0; var shift = 0;
                while (i < g.Length)
                {
                    var c = g[i++];
                    v |= (ulong)(c & 0x7f) << shift;
                    if ((c & 0x80) == 0) break;
                    shift += 7;
                }
                return v;
            }
            string Rs(int len)
            {
                if (len <= 0 || i + len > g.Length) { i = g.Length; return ""; }
                var s = Encoding.UTF8.GetString(g, i, len);
                i += len;
                return s;
            }
            while (i < g.Length)
            {
                var tag = Rv();
                var fn = (int)(tag >> 3);
                var wt = (int)(tag & 7);
                if (wt == 0)
                {
                    var v = (long)Rv();
                    if (fn == 3) numA = v;
                    else if (fn == 5) price = v;
                    else if (fn == 14) totalCoin = v;
                    else if (fn == 17) numB = v;
                }
                else if (wt == 2)
                {
                    var len = (int)Rv();
                    if (fn == 2) giftName = Rs(len);
                    else if (fn == 8) coinType = Rs(len);
                    else Rs(len);
                }
                else if (wt == 5) i += 4;
                else if (wt == 1) i += 8;
                else break;
            }
        }

        try
        {
            while (pos < buf.Length)
            {
                var tag = ReadVarint();
                var field = (int)(tag >> 3);
                var wire = (int)(tag & 7);
                if (wire == 0)
                {
                    var v = ReadVarint();
                    if (field == 1) uid = v.ToString();
                }
                else if (wire == 2)
                {
                    var len = (int)ReadVarint();
                    if (field == 2) uname = Encoding.UTF8.GetString(ReadBytes(len));
                    else if (field == 8) medal = ParseMedalLevel(ReadBytes(len));
                    else if (field == 10) ParseGiftMessage(ReadBytes(len));
                    else ReadBytes(len);
                }
                else if (wire == 5) pos += 4;
                else if (wire == 1) pos += 8;
                else break;
            }
        }
        catch { }

        long num;
        if (price > 0 && totalCoin > 0 && totalCoin % price == 0) num = totalCoin / price;
        else num = Math.Max(numA, numB);
        if (num < 1) num = 1;
        if (price > 0 && totalCoin == 0) totalCoin = num * price;
        return (uid, uname, giftName, (int)num, price, totalCoin, coinType, medal);
    }

    /// <summary>Medal submessage of a V2 gift: f5 carries the 粉丝团 level.</summary>
    private static int ParseMedalLevel(byte[] m)
    {
        var i = 0;
        while (i < m.Length)
        {
            var tag = 0; var shift = 0;
            while (i < m.Length)
            {
                var c = m[i++];
                tag |= (c & 0x7f) << shift;
                if ((c & 0x80) == 0) break;
                shift += 7;
            }
            var fn = tag >> 3;
            var wt = tag & 7;
            if (wt == 0)
            {
                var v = 0; shift = 0;
                while (i < m.Length)
                {
                    var c = m[i++];
                    v |= (c & 0x7f) << shift;
                    if ((c & 0x80) == 0) break;
                    shift += 7;
                }
                if (fn == 5) return v;
            }
            else if (wt == 2)
            {
                var len = 0; shift = 0;
                while (i < m.Length)
                {
                    var c = m[i++];
                    len |= (c & 0x7f) << shift;
                    if ((c & 0x80) == 0) break;
                    shift += 7;
                }
                i += len;
            }
            else if (wt == 5) i += 4;
            else if (wt == 1) i += 8;
            else break;
        }
        return 0;
    }

    private static JsonElement GetProp(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : default;

    private static string GetStr(JsonElement el, string name)
    {
        var v = GetProp(el, name);
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static long GetLong(JsonElement el, string name, long def = 0)
    {
        var v = GetProp(el, name);
        return v.ValueKind == JsonValueKind.Number ? v.GetInt64() : def;
    }

    private static string Plain(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.String => el.GetString() ?? "",
        _ => "",
    };

    private static (int Medal, int Honor) ExtractTier(JsonElement root)
    {
        int medal = 0, honor = 0;
        Walk(root);
        return (medal, honor);

        void Walk(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    var hasMedal = false;
                    var hasHonor = false;
                    foreach (var p in el.EnumerateObject())
                    {
                        if (p.Name is "medal" or "fans_medal" or "fan_medal" or "fans_medal_name" or "medal_name") hasMedal = true;
                        if (p.Name is "honor" or "honour" or "honor_rank" or "honor_level" or "guard_level") hasHonor = true;
                    }
                    if (hasMedal)
                    {
                        var lv = 0;
                        if (el.TryGetProperty("level", out var lvl) && lvl.ValueKind == JsonValueKind.Number) lv = lvl.GetInt32();
                        else if (el.TryGetProperty("medal", out var medalEl) && medalEl.ValueKind == JsonValueKind.Object
                                 && medalEl.TryGetProperty("level", out var ml) && ml.ValueKind == JsonValueKind.Number) lv = ml.GetInt32();
                        if (lv > medal) medal = lv;
                    }
                    if (hasHonor)
                    {
                        var lv = 0;
                        if (el.TryGetProperty("level", out var lvl) && lvl.ValueKind == JsonValueKind.Number) lv = lvl.GetInt32();
                        else if (el.TryGetProperty("honor", out var honorEl))
                        {
                            if (honorEl.ValueKind == JsonValueKind.Number) lv = honorEl.GetInt32();
                            else if (honorEl.ValueKind == JsonValueKind.Object && honorEl.TryGetProperty("level", out var hl) && hl.ValueKind == JsonValueKind.Number) lv = hl.GetInt32();
                        }
                        else if (el.TryGetProperty("guard_level", out var gl) && gl.ValueKind == JsonValueKind.Number) lv = gl.GetInt32();
                        if (lv > honor) honor = lv;
                    }
                    foreach (var p in el.EnumerateObject()) Walk(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray()) Walk(item);
                    break;
            }
        }
    }

    private static (bool IsGuard, int Level) DetectGuard(JsonElement root)
    {
        var found = Walk(root);
        if (found.IsGuard) return found;
        try
        {
            // Keyword fallback mirrors bili.js JSON.stringify regex test.
            var raw = root.GetRawText();
            if (raw.Contains("总督", StringComparison.Ordinal)) return (true, 1);
            if (raw.Contains("提督", StringComparison.Ordinal)) return (true, 2);
            if (raw.Contains("舰长", StringComparison.Ordinal) || raw.Contains("守护", StringComparison.Ordinal)) return (true, 3);
        }
        catch { }
        return (false, 0);

        (bool IsGuard, int Level) Walk(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in el.EnumerateObject())
                    {
                        if (p.Name is "guard_level" or "guard_rank" or "rank" && p.Value.ValueKind == JsonValueKind.Number)
                        {
                            var v = p.Value.GetInt32();
                            if (v >= 1 && v <= 3) return (true, v);
                        }
                    }
                    foreach (var p in el.EnumerateObject())
                    {
                        var r = Walk(p.Value);
                        if (r.Item1) return r;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        var r = Walk(item);
                        if (r.Item1) return r;
                    }
                    break;
            }
            return (false, 0);
        }
    }
}
