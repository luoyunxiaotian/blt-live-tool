using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Embedded HTTP/WS server — in-process replacement of Bin/server.js.
/// Phase 3: the original API surface is ported (recorder/logs, auto-danmu
/// stats, song request, lyrics, TTS proxy, widgets, alert, PK, like, monitor,
/// diagnostics); remaining gaps fall back to JSON stubs so the untouched
/// panel JS degrades gracefully.
/// </summary>
public sealed class KestrelHost
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppConfig _config;
    private readonly EventHub _hub;
    private readonly LiveService _live;
    private readonly Recorder _recorder;
    private readonly LivePipeline _pipeline;
    private readonly TtsHost _tts;
    private readonly MusicLoginService _musicLogin;
    private readonly KeyViewService _keyview;
    private readonly UpdateChecker _updateChecker = new(VersionText);
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> _keyviewClients = new();
    private readonly HttpClient _proxy = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly Random _rand = new();
    private WebApplication? _app;

    public const string VersionText = "1.0.0-maui";

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public int Port => _config.Port;

    public KestrelHost(AppConfig config, EventHub hub, LiveService live, Recorder recorder, LivePipeline pipeline, TtsHost tts, MusicLoginService musicLogin, KeyViewService keyview)
    {
        _config = config;
        _hub = hub;
        _live = live;
        _recorder = recorder;
        _pipeline = pipeline;
        _tts = tts;
        _musicLogin = musicLogin;
        _keyview = keyview;
    }

    public void StartInBackground()
    {
        _ = Task.Run(async () =>
        {
            try { await StartAsync(); }
            catch (Exception ex) { LastError = ex.Message; IsRunning = false; }
        });
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{_config.Port}");

        var app = builder.Build();
        app.UseWebSockets();

        // Browser entry opens the legacy panel; KeyView overlays connect a
        // WebSocket on this same root path (the original keyview server did
        // the same on port 7788).
        app.MapGet("/", async (HttpContext ctx) =>
        {
            if (ctx.WebSockets.IsWebSocketRequest)
            {
                var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                var id = Guid.NewGuid();
                _keyviewClients[id] = ws;
                try
                {
                    // On connect: push the merged config ({t:'cfg',full}), same
                    // as the original keyview server; 'hello' re-requests it.
                    await SendKeyView(ws, JsonSerializer.Serialize(new { t = "cfg", full = _keyview.Config.GetAll() }, JsonWeb));
                    var buf = new byte[2048];
                    while (ws.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
                    {
                        var res = await ws.ReceiveAsync(buf, ctx.RequestAborted);
                        if (res.MessageType == WebSocketMessageType.Close) break;
                        if (res.Count > 0)
                        {
                            var text = Encoding.UTF8.GetString(buf, 0, res.Count);
                            if (text.Contains("\"hello\""))
                                await SendKeyView(ws, JsonSerializer.Serialize(new { t = "cfg", full = _keyview.Config.GetAll() }, JsonWeb));
                        }
                    }
                }
                catch { }
                finally
                {
                    _keyviewClients.TryRemove(id, out _);
                    try { ws.Dispose(); } catch { }
                }
                return;
            }
            ctx.Response.Redirect("/legacy/index.html");
        });

        // KeyView overlay fetches the merged config from the server root.
        app.MapGet("/config", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            return Results.Json(_keyview.Config.GetAll(), JsonWeb);
        });

        app.UseStaticFiles();

        // Original root-level overlay URLs keep working after migration.
        foreach (var dir in new[] { "alert", "lyrics", "widgets", "keyview", "sounds", "skins" })
        {
            var full = Path.Combine(AppConfig.LegacyRoot, dir);
            if (Directory.Exists(full))
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(full),
                    RequestPath = "/" + dir,
                });
        }

        MapApi(app);
        MapWs(app);

        _hub.OnEvent += OnHubEvent;
        _hub.StatusChanged += OnHubStatus;
        _hub.OnOutbound += OnHubOutbound;

        _keyview.OnEventJson += BroadcastKeyViewJson;
        _keyview.OnConfigFrame += BroadcastKeyViewJson;
        _keyview.ClientCount = () => _keyviewClients.Count;

        _app = app;
        await app.StartAsync();
        IsRunning = true;
        LastError = null;
    }

    // ---------------- API ----------------

    private void MapApi(WebApplication app)
    {
        // ---- config / status / connection ----
        app.MapGet("/api/config", () => Results.Json(_config.Snapshot(), JsonWeb));
        app.MapPost("/api/config", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb);
                if (body != null)
                {
                    _config.UpdateFrom(body);
                    ApplyRecordingFlags();
                    _pipeline.ApplyConfig();
                }
                return Results.Json(_config.Snapshot(), JsonWeb);
            }
            catch { return Results.Json(new { error = "bad json" }, JsonWeb, statusCode: 400); }
        });

        app.MapGet("/api/status", () => Results.Json(StatusPayload(), JsonWeb));

        app.MapPost("/api/connect", async (HttpContext ctx) =>
        {
            ConnectBody? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<ConnectBody>(JsonWeb); } catch { }
            var roomId = (body?.RoomId ?? "").Trim();
            var cookie = (body?.Cookie ?? "").Trim();
            if (roomId.Length == 0) roomId = _config.RoomId;
            if (roomId.Length == 0) return Results.Json(new { error = "缺少 roomId" }, JsonWeb, statusCode: 400);
            _config.SetRoomAndCookie(roomId, cookie);
            _live.Start(roomId, cookie.Length > 0 ? cookie : _config.Cookie);
            return Results.Json(StatusPayload(), JsonWeb);
        });

        app.MapPost("/api/disconnect", () =>
        {
            _live.Stop();
            return Results.Json(StatusPayload(), JsonWeb);
        });

        // Verification lock is an Electron-only mechanism; web mode never locks.
        app.MapPost("/api/verify-lock", () => Results.Json(new { ok = true, locked = false }, JsonWeb));

        app.MapGet("/api/recent", (HttpContext ctx) =>
        {
            var type = ctx.Request.Query["type"].ToString();
            if (string.IsNullOrEmpty(type)) type = "danmu";
            return Results.Json(new { type, events = _hub.GetRecent(type) }, JsonWeb);
        });

        // ---- recorder / logs ----
        app.MapGet("/api/logs/{type}", (HttpContext ctx) =>
        {
            var type = ctx.Request.RouteValues["type"]?.ToString() ?? "";
            if (!Recorder.Types.Contains(type)) return Results.Json(new { error = "bad type" }, JsonWeb, statusCode: 400);
            return Results.Json(_recorder.Query(type,
                ctx.Request.Query["start"], ctx.Request.Query["end"], ctx.Request.Query["uid"], ctx.Request.Query["q"],
                ParseInt(ctx.Request.Query["page"], 1), ParseInt(ctx.Request.Query["size"], 50)), JsonWeb);
        });
        app.MapGet("/api/files/{type}", (HttpContext ctx) =>
        {
            var type = ctx.Request.RouteValues["type"]?.ToString() ?? "";
            if (!Recorder.Types.Contains(type)) return Results.Json(new { error = "bad type" }, JsonWeb, statusCode: 400);
            return Results.Json(_recorder.ListFiles(type), JsonWeb);
        });
        app.MapPost("/api/open-folder/{type}", (HttpContext ctx) =>
        {
            var type = ctx.Request.RouteValues["type"]?.ToString() ?? "";
            if (!Recorder.Types.Contains(type)) return Results.Json(new { error = "bad type" }, JsonWeb, statusCode: 400);
            return Results.Json(new { ok = true, dir = _recorder.OpenFolder(type) }, JsonWeb);
        });
        app.MapPost("/api/open-path", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb);
                var p = body?["path"]?.GetValue<string>() ?? "";
                Recorder.OpenInExplorer(p);
                return Results.Json(new { ok = true, dir = p }, JsonWeb);
            }
            catch { return Results.Json(new { ok = false, dir = "" }, JsonWeb); }
        });
        app.MapPost("/api/list-dir", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb);
                var dir = body?["dir"]?.GetValue<string>();
                if (string.IsNullOrEmpty(dir)) dir = AppConfig.DataDir;
                var entries = new List<object>();
                foreach (var full in Directory.GetDirectories(dir))
                    entries.Add(new { name = Path.GetFileName(full), dir = true, size = 0L, mtime = File.GetLastWriteTime(full).ToString("yyyy-MM-dd HH:mm:ss") });
                foreach (var full in Directory.GetFiles(dir))
                    entries.Add(new { name = Path.GetFileName(full), dir = false, size = new FileInfo(full).Length, mtime = File.GetLastWriteTime(full).ToString("yyyy-MM-dd HH:mm:ss") });
                return Results.Json(new { dir, entries }, JsonWeb);
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb); }
        });
        app.MapPost("/api/recording/{type}", async (HttpContext ctx, string type) =>
        {
            if (!Recorder.Types.Contains(type)) return Results.Json(new { error = "bad type" }, JsonWeb, statusCode: 400);
            bool on = false;
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb);
                on = body?.TryGetPropertyValue("on", out var onEl) == true && onEl is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            }
            catch { }
            _recorder.SetEnabled(type, on);
            _config.SetRecording(type, on);
            return Results.Json(new { type, on }, JsonWeb);
        });
        app.MapPost("/api/export/{type}", (HttpContext ctx) =>
        {
            var type = ctx.Request.RouteValues["type"]?.ToString() ?? "";
            if (!Recorder.Types.Contains(type)) return Results.Json(new { error = "bad type" }, JsonWeb, statusCode: 400);
            try
            {
                _recorder.RegenerateToday(type);
                return Results.Json(new { ok = true }, JsonWeb);
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
        });
        app.MapPost("/api/raw", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb) ?? new JsonObject();
                var type = body["type"]?.GetValue<string>() ?? "";
                if (!Recorder.Types.Contains(type)) return Results.Json(new { error = "bad type" }, JsonWeb, statusCode: 400);
                var ev = new LiveEvent
                {
                    Type = type,
                    Time = Recorder.Now(),
                    Ts = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                    Uid = body["uid"]?.GetValue<string>() ?? "",
                    Uname = body["uname"]?.GetValue<string>() ?? "",
                    Msg = body["msg"]?.GetValue<string>() ?? "",
                    GiftName = body["giftName"]?.GetValue<string>() ?? "",
                };
                if (body["num"] is JsonValue nv) ev.Num = (int)nv.GetValue<long>();
                _recorder.Record(type, ev);
                return Results.Json(new { ok = true }, JsonWeb);
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
        });

        // ---- pk ----
        app.MapGet("/api/pk-status", () => Results.Json(_pipeline.PkStatus(), JsonWeb));
        app.MapPost("/api/pk-target", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb);
                var rid = (body?["roomId"]?.GetValue<string>() ?? "").Trim();
                if (rid.Length == 0) return Results.Json(new { error = "缺少 roomId" }, JsonWeb, statusCode: 400);
                // PK opponent joins the monitor list by default (same as server.js).
                if (Regex.IsMatch(rid, "^\\d+$"))
                {
                    var doc = _config.Snapshot();
                    var rooms = (doc["monitoredRooms"] as JsonArray) ?? new JsonArray();
                    doc["monitoredRooms"] = rooms;
                    if (!rooms.Any(r => (r as JsonObject)?["roomId"]?.GetValue<string>() == rid) && rooms.Count < 8)
                    {
                        rooms.Add(new JsonObject { ["roomId"] = rid, ["enabled"] = true });
                        _config.ReplaceFrom(doc);
                    }
                }
                return Results.Json(await _pipeline.SetPkTargetAsync(rid), JsonWeb);
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
        });

        // ---- auto like ----
        app.MapPost("/api/like/start", () => Results.Json(_pipeline.LikeStart(), JsonWeb));
        app.MapGet("/api/like/status", () => Results.Json(_pipeline.LikeStatus(), JsonWeb));
        app.MapPost("/api/like/stop", () => Results.Json(_pipeline.LikeStop(), JsonWeb));

        // ---- room live info / monitor ----
        app.MapGet("/api/room/live-info", async (HttpContext ctx) =>
        {
            var roomId = _config.RoomId;
            if (!long.TryParse(roomId, out var rid))
                return Results.Json(new { online = 0, guardTotal = 0, guardCount = new long[3], top3 = Array.Empty<object>(), onlineRank = Array.Empty<object>() }, JsonWeb);
            long.TryParse(_hub.LastStatus.AnchorUid, out var anchorUid);
            try
            {
                var info = await BiliApi.GetRoomLiveInfoAsync(rid, anchorUid, ctx.RequestAborted);
                return Results.Json(new
                {
                    online = info.Online > 0 ? info.Online : _hub.LastStatus.Popularity,
                    guardTotal = info.GuardTotal,
                    guardCount = info.GuardCount,
                    top3 = info.Top3,
                    onlineRank = Array.Empty<object>(),
                }, JsonWeb);
            }
            catch { return Results.Json(new { online = 0, guardTotal = 0, guardCount = new long[3], top3 = Array.Empty<object>(), onlineRank = Array.Empty<object>() }, JsonWeb); }
        });
        app.MapGet("/api/rooms/monitor", () => Results.Json(_pipeline.MonitorGet(), JsonWeb));
        app.MapPost("/api/rooms/monitor", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb) ?? new JsonObject();
                return Results.Json(await _pipeline.MonitorPostAsync(body), JsonWeb);
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 400); }
        });

        // ---- blindbox ----
        app.MapPost("/api/blindbox/detect", async (HttpContext ctx) =>
        {
            var ridStr = _hub.LastStatus.RealRoomId;
            if (string.IsNullOrEmpty(ridStr) || ridStr == "0") ridStr = _config.RoomId;
            if (string.IsNullOrEmpty(ridStr)) return Results.Json(new { error = "未连接直播间" }, JsonWeb, statusCode: 400);
            try
            {
                var boxes = await BiliApi.AutoDetectBlindBoxesAsync(long.Parse(ridStr), _config.Cookie, ctx.RequestAborted);
                return Results.Json(new { boxes }, JsonWeb);
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
        });
        app.MapGet("/api/blindbox/builtin", () => Results.Json(_recorder.BlindBoxData, JsonWeb));
        app.MapPost("/api/blindbox/save", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb);
                var data = body?["data"] as JsonObject;
                if (data == null) return Results.Json(new { error = "缺少 data" }, JsonWeb, statusCode: 400);
                _recorder.SaveBlindBoxData(data);
                return Results.Json(new { ok = true }, JsonWeb);
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
        });

        // ---- debug ----
        app.MapPost("/api/debug/event", async (HttpContext ctx) =>
        {
            JsonObject? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb); } catch { }
            var type = body?.TryGetPropertyValue("type", out var t) == true ? t?.GetValue<string>() : "danmu";
            var ev = BuildMockEvent(type ?? "danmu", body);
            if (ev == null)
                return Results.Json(new { error = "不支持的事件类型：" + type + "，可用：danmu / gifts / interact / follow / guard / superchat" }, JsonWeb, statusCode: 400);
            _pipeline.ProcessDebugEvent(ev);
            return Results.Json(new { ok = true, @event = ev }, JsonWeb);
        });
        app.MapGet("/api/debug/stats", () => Results.Json(_pipeline.DebugStats(), JsonWeb));
        app.MapPost("/api/debug/client-log", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb);
                var line = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + " " + (body?["msg"]?.GetValue<string>() ?? "");
                if (line.Length > 520) line = line[..520];
                Directory.CreateDirectory(AppConfig.DataDir);
                File.AppendAllText(Path.Combine(AppConfig.DataDir, "client-log.txt"), line + "\n");
            }
            catch { }
            return Results.Json(new { ok = true }, JsonWeb);
        });

        // ---- auto danmu ----
        app.MapGet("/api/autodanmu/follow-stats", () =>
        {
            var st = _pipeline.AutoDanmu.FollowStats();
            return Results.Json(new
            {
                ok = true,
                enabled = ((_config.GetNode("autoDanmu") as JsonObject)?["follow"] as JsonObject)?
                    .TryGetPropertyValue("enabled", out var en) == true && en is JsonValue v && v.TryGetValue<bool>(out var b) && b,
                count = st.GetType().GetProperty("count")!.GetValue(st),
                tracked = st.GetType().GetProperty("tracked")!.GetValue(st),
                cap = st.GetType().GetProperty("cap")!.GetValue(st),
            }, JsonWeb);
        });

        // ---- song request ----
        app.MapGet("/api/song-request/playlist", () => Results.Json(_pipeline.SongRequest.PlaylistPayload(), JsonWeb));
        app.MapPost("/api/song-request/playlist/add", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb);
                var song = body?["song"] as JsonObject;
                if (song == null) return Results.Json(new { error = "缺少 song" }, JsonWeb, statusCode: 400);
                return Results.Json(_pipeline.SongRequest.AddToPlaylistManual(song, body?["requester"]?.GetValue<string>() ?? ""), JsonWeb);
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
        });
        app.MapPost("/api/song-request/playlist/remove", async (HttpContext ctx) =>
        {
            var body = await ReadJsonObject(ctx);
            return Results.Json(_pipeline.SongRequest.RemoveFromPlaylist((int)(body?["index"]?.GetValue<long?>() ?? -1)), JsonWeb);
        });
        app.MapPost("/api/song-request/playlist/reorder", async (HttpContext ctx) =>
        {
            var body = await ReadJsonObject(ctx);
            return Results.Json(_pipeline.SongRequest.ReorderPlaylist(
                (int)(body?["from"]?.GetValue<long?>() ?? -1), (int)(body?["to"]?.GetValue<long?>() ?? -1)), JsonWeb);
        });
        app.MapPost("/api/song-request/playlist/clear", () => Results.Json(_pipeline.SongRequest.ClearPlaylist(), JsonWeb));
        app.MapPost("/api/song-request/skip", () => Results.Json(_pipeline.SongRequest.SkipCurrent(), JsonWeb));
        app.MapGet("/api/song-request/recent", () => Results.Json(_pipeline.SongRequest.Recent(), JsonWeb));
        app.MapPost("/api/song-request/search", async (HttpContext ctx) =>
        {
            var body = await ReadJsonObject(ctx);
            var keyword = body?["keyword"]?.GetValue<string>() ?? "";
            if (keyword.Length == 0) return Results.Json(new { error = "缺少 keyword" }, JsonWeb, statusCode: 400);
            try
            {
                var results = await _pipeline.SongRequest.SearchAsync(keyword, body?["type"]?.GetValue<string>(), ctx.RequestAborted);
                return Results.Json(new { results }, JsonWeb);
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
        });
        app.MapPost("/api/song-request/song-url", async (HttpContext ctx) =>
        {
            var body = await ReadJsonObject(ctx);
            if (!body.ContainsKey("index")) return Results.Json(new { error = "缺少 index" }, JsonWeb, statusCode: 400);
            try
            {
                var result = await _pipeline.SongRequest.GetSongUrlAsync((int)(body["index"]?.GetValue<long?>() ?? -1), ctx.RequestAborted);
                // B站音频有防盗链，走本地代理播放（与原版一致）
                var resultNode = JsonNode.Parse(JsonSerializer.Serialize(result, JsonWeb)) as JsonObject;
                if (resultNode?["url"] is JsonValue uv && uv.TryGetValue<string>(out var url) &&
                    resultNode?["song"]?["platform"]?.GetValue<string>() == "bilibili" && url.Length > 0)
                    resultNode["url"] = "/api/song-request/bilibili-audio?u=" + Uri.EscapeDataString(url);
                return Results.Json(resultNode, JsonWeb);
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
        });
        app.MapGet("/api/song-request/bilibili-audio", async (HttpContext ctx) =>
        {
            var audioUrl = ctx.Request.Query["u"].ToString();
            if (audioUrl.Length == 0 || !Regex.IsMatch(audioUrl, "^https?://.+bilivideo\\.com/"))
                return Results.Json(new { error = "无效的音频URL" }, JsonWeb, statusCode: 400);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, audioUrl);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
                using var resp = await _proxy.GetAsync(req.RequestUri!, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                if (!resp.IsSuccessStatusCode)
                    return Results.Json(new { error = "B站音频流返回: " + (int)resp.StatusCode }, JsonWeb, statusCode: 502);
                ctx.Response.ContentType = "audio/mp4";
                await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                return Results.Empty;
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
        });
        app.MapGet("/api/song-request/blacklist", () => Results.Json(new { blacklist = _pipeline.SongRequest.Blacklist() }, JsonWeb));
        app.MapPost("/api/song-request/blacklist", async (HttpContext ctx) =>
        {
            var body = await ReadJsonObject(ctx);
            var type = body?["type"]?.GetValue<string>() ?? "";
            var value = body?["value"]?.GetValue<string>() ?? "";
            if (type.Length == 0 || value.Length == 0) return Results.Json(new { error = "缺少 type/value" }, JsonWeb, statusCode: 400);
            var bl = _pipeline.SongRequest.Blacklist();
            bl.Add(new JsonObject { ["type"] = type, ["value"] = value });
            _pipeline.SongRequest.SaveBlacklist(bl);
            return Results.Json(new { ok = true, blacklist = bl }, JsonWeb);
        });
        app.MapPost("/api/song-request/blacklist/delete", async (HttpContext ctx) =>
        {
            var body = await ReadJsonObject(ctx);
            if (!body.ContainsKey("index")) return Results.Json(new { error = "缺少 index" }, JsonWeb, statusCode: 400);
            var bl = _pipeline.SongRequest.Blacklist();
            var idx = (int)(body["index"]?.GetValue<long?>() ?? -1);
            if (idx >= 0 && idx < bl.Count) bl.RemoveAt(idx);
            _pipeline.SongRequest.SaveBlacklist(bl);
            return Results.Json(new { ok = true, blacklist = bl }, JsonWeb);
        });
        app.MapPost("/api/song-request/cookie", async (HttpContext ctx) =>
        {
            var body = await ReadJsonObject(ctx);
            var platform = body["platform"]?.GetValue<string>() ?? "";
            if (platform.Length == 0 || !body.ContainsKey("cookie")) return Results.Json(new { error = "缺少 platform/cookie" }, JsonWeb, statusCode: 400);
            var cookieVal = body["cookie"] is JsonValue cv ? cv.GetValue<string>() ?? "" : "";
            _pipeline.SongRequest.SavePlatformCookie(platform, cookieVal);
            return Results.Json(new { ok = true }, JsonWeb);
        });
        app.MapGet("/api/song-request/cookie-status", () => Results.Json(_pipeline.SongRequest.CookieStatus(), JsonWeb));
        app.MapGet("/api/song-request/bilibili-uplist", () => Results.Json(new { upList = _pipeline.SongRequest.UpList() }, JsonWeb));
        app.MapPost("/api/song-request/bilibili-uplist/add", async (HttpContext ctx) =>
        {
            var body = await ReadJsonObject(ctx);
            var uid = (body?["uid"]?.GetValue<string>() ?? "").Trim();
            if (uid.Length == 0) return Results.Json(new { error = "缺少 uid" }, JsonWeb, statusCode: 400);
            var list = _pipeline.SongRequest.UpList();
            if (!list.Any(x => x?.GetValue<string>() == uid)) list.Add(uid);
            _pipeline.SongRequest.SetUpList(list);
            return Results.Json(new { ok = true, upList = list }, JsonWeb);
        });
        app.MapPost("/api/song-request/bilibili-uplist/delete", async (HttpContext ctx) =>
        {
            var body = await ReadJsonObject(ctx);
            var uid = (body?["uid"]?.GetValue<string>() ?? "").Trim();
            var list = _pipeline.SongRequest.UpList();
            var node = list.FirstOrDefault(x => x?.GetValue<string>() == uid);
            if (node != null) list.Remove(node);
            _pipeline.SongRequest.SetUpList(list);
            return Results.Json(new { ok = true, upList = list }, JsonWeb);
        });

        // ---- lyrics (local → netease → qq → kugou) ----
        app.MapGet("/api/lyrics", async (HttpContext ctx) =>
        {
            var song = ctx.Request.Query["song"].ToString().Trim();
            var artist = ctx.Request.Query["artist"].ToString().Trim();
            var platform = ctx.Request.Query["platform"].ToString().Trim();
            var id = ctx.Request.Query["id"].ToString().Trim();
            try
            {
                var (source, lrc) = await MusicApi.GetLyricsAsync(song, artist, platform, id, AppConfig.DataDir, ctx.RequestAborted);
                return Results.Json(new { ok = true, source, lrc }, JsonWeb);
            }
            catch
            {
                return Results.Json(new { ok = true, source = "none", lrc = "" }, JsonWeb);
            }
        });

        // ---- alert wall / sounds ----
        app.MapPost("/api/alert/test", async (HttpContext ctx) =>
        {
            JsonObject? body = null;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb); } catch { }
            var t = body?.TryGetPropertyValue("type", out var ty) == true ? ty?.GetValue<string>() : "gift";
            object sample = t switch
            {
                "guard" => new LiveEvent { Type = "guard", Uname = "测试舰长", LevelName = "舰长", Num = 1, Value = 138 },
                "superchat" => new LiveEvent { Type = "superchat", Uname = "测试SC", Msg = "主播加油！", Price = 30 },
                "follow" => new LiveEvent { Type = "interact", Uname = "测试关注", MsgType = 2 },
                "enter" => new LiveEvent { Type = "interact", Uname = "测试观众", MsgType = 1 },
                _ => new LiveEvent { Type = "gifts", Uname = "测试观众", GiftName = "小花花", Num = 1, Value = 0.1 },
            };
            _hub.PublishOutbound("alert_test", sample);
            return Results.Json(new { ok = true }, JsonWeb);
        });
        app.MapGet("/api/sounds", () =>
        {
            var names = new SortedSet<string>();
            try
            {
                foreach (var f in Directory.GetFiles(Path.Combine(AppConfig.LegacyRoot, "sounds")))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".wav" or ".ogg" or ".mp3") names.Add(Path.GetFileName(f));
                }
            }
            catch { }
            try
            {
                foreach (var f in Directory.GetFiles(Path.Combine(AppConfig.DataDir, "sounds")))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".wav" or ".ogg" or ".mp3") names.Add(Path.GetFileName(f));
                }
            }
            catch { }
            return Results.Json(new { ok = true, sounds = names.ToArray() }, JsonWeb);
        });

        // ---- widgets (OBS text overlays) ----
        app.MapGet("/api/widgets", () => Results.Json(new { ok = true, widgets = _config.GetNode("widgets") ?? new JsonArray() }, JsonWeb));
        app.MapPost("/api/widgets", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ReadJsonObject(ctx);
                if (body?["widgets"] is not JsonArray list) return Results.Json(new { error = "缺少 widgets" }, JsonWeb, statusCode: 400);
                if (list.Count > 10) return Results.Json(new { error = "最多 10 个挂件" }, JsonWeb, statusCode: 400);
                var cleaned = new JsonArray();
                var ids = new HashSet<string>();
                for (var i = 0; i < list.Count; i++)
                {
                    var w = list[i] as JsonObject;
                    if (w == null) continue;
                    var id = Regex.Replace(w["id"]?.GetValue<string>() ?? "", "[^a-zA-Z0-9_-]", "");
                    if (id.Length > 24) id = id[..24];
                    if (id.Length == 0) id = "w" + DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString("x36") + i;
                    while (ids.Contains(id)) id = "w" + Guid.NewGuid().ToString("N")[..6];
                    ids.Add(id);
                    cleaned.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["name"] = Clip(SafeStr(w["name"]), 20, "挂件"),
                        ["text"] = Clip(SafeStr(w["text"]), 2000, ""),
                        ["effect"] = InSet(SafeStr(w["effect"]), "static", "marquee", "typewriter", "flipX", "flipY", "blink"),
                        ["speed"] = Clamp(SafeD(w["speed"], 10), 0.1, 120),
                        ["align"] = InSet(SafeStr(w["align"]), "left", "center", "right"),
                        ["fontSize"] = Clamp(SafeD(w["fontSize"], 28), 12, 120),
                        ["font"] = Clip(SafeStr(w["font"]), 40, "微软雅黑"),
                        ["color"] = HexColor(SafeStr(w["color"]), "#ffffff"),
                        ["textOpacity"] = Clamp(SafeD(w["textOpacity"], 1), 0, 1),
                        ["outline"] = InSet(SafeStr(w["outline"]), "none", "black", "white", "custom"),
                        ["outlineColor"] = HexColor(SafeStr(w["outlineColor"]), "#000000"),
                        ["outlineOpacity"] = Clamp(SafeD(w["outlineOpacity"], 1), 0, 1),
                        ["outlineWidth"] = Clamp(SafeD(w["outlineWidth"], 2), 0, 8),
                        ["bgColor"] = HexColor(SafeStr(w["bgColor"]), "#fb7299"),
                        ["bgOpacity"] = Clamp(SafeD(w["bgOpacity"], 0.6), 0, 1),
                        ["rounded"] = Clamp(SafeD(w["rounded"], 8), 0, 40),
                    });
                }
                var doc = _config.Snapshot();
                doc["widgets"] = cleaned;
                _config.ReplaceFrom(doc);
                _hub.PublishOutbound("widgets", null);   // overlay pages hot-reload
                return Results.Json(new { ok = true, widgets = cleaned }, JsonWeb);
            }
            catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
        });

        // ---- diagnostics ----
        app.MapPost("/api/diagnostics", async (HttpContext ctx) =>
        {
            var room = _hub.LastStatus.RealRoomId;
            if (string.IsNullOrEmpty(room) || room == "0") room = _config.RoomId;
            if (string.IsNullOrEmpty(room)) room = "6";
            var cookie = _config.Cookie;

            async Task<object> Probe(string name, string url, Dictionary<string, string>? headers, Func<JsonNode?, long, object> judge)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (headers != null)
                        foreach (var (k, v) in headers)
                            req.Headers.TryAddWithoutValidation(k, v);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    cts.CancelAfter(6000);
                    using var resp = await _proxy.SendAsync(req, cts.Token);
                    sw.Stop();
                    JsonNode? j = null;
                    try { j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token)); } catch { }
                    var extra = judge(j, (long)resp.StatusCode);
                    var node = JsonNode.Parse(JsonSerializer.Serialize(extra, JsonWeb)) as JsonObject;
                    node!["name"] = name;
                    node["state"] ??= "ok";
                    node["latency"] = sw.ElapsedMilliseconds;
                    node["detail"] ??= "可达 · HTTP " + resp.StatusCode;
                    return node;
                }
                catch (Exception e)
                {
                    return new { name, state = "fail", latency = sw.ElapsedMilliseconds, detail = e.Message };
                }
            }

            var httpHeaders = new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0 bili-live-tool" };
            if (cookie.Length > 0) httpHeaders["Cookie"] = cookie;
            var room_ = room;
            var results = new List<object>
            {
                await Probe("B站房间接口（观众数/房间信息）",
                    "https://api.live.bilibili.com/room/v1/Room/get_info?room_id=" + Uri.EscapeDataString(room_), httpHeaders, (j, _) =>
                    {
                        if ((j?["code"]?.GetValue<long?>() ?? -1) == 0)
                            return new { detail = "正常 · 房间在线 " + (j?["data"]?["online"]?.GetValue<long?>() ?? 0) };
                        return new { state = "warn", detail = "网络可达，但接口返回 code=" + (j?["code"]?.ToString() ?? "?") + " " + (j?["message"]?.GetValue<string>() ?? "") };
                    }),
                await Probe("B站弹幕服务器握手接口",
                    "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?id=" + Uri.EscapeDataString(room_), httpHeaders, (j, _) =>
                    {
                        if ((j?["code"]?.GetValue<long?>() ?? -1) == 0)
                            return new { detail = "正常 · 弹幕节点 " + (j?["data"]?["host_list"]?[0]?["host"]?.GetValue<string>() ?? "已分配") };
                        return new { state = "warn", detail = "网络可达，但接口返回 code=" + (j?["code"]?.ToString() ?? "?") + (cookie.Length == 0 ? "（未登录可能被拒）" : "") };
                    }),
                await Probe("授权验证服务器", "https://ai-daynews.xyz/", null, (_, _) => new { detail = "可达" }),
                await Probe("GitHub 更新源", "https://api.github.com/repos/luoyunxiaotian/bili-live-tool/releases/latest",
                    new Dictionary<string, string> { ["User-Agent"] = "bili-live-tool" },
                    (j, _) => new { detail = "可达 · 最新发布 " + (j?["tag_name"]?.GetValue<string>() ?? "未知") }),
                await Probe("网易云音乐源（点歌）", "https://music.163.com/", null, (_, _) => new { detail = "可达" }),
                await Probe("Edge TTS 语音服务（本机 8020）", "http://127.0.0.1:8020/", null, (_, status) => new { detail = "服务运行中 · HTTP " + status }),
                await Probe("本地语音引擎（MOSS 8021）", "http://127.0.0.1:8021/health", null, (j, status) => new { detail = "服务运行中 · " + ((j?["modelsReady"]?.GetValue<bool?>() ?? false) ? "模型已就绪" : "模型未下载") }),
                new
                {
                    name = "本机服务端",
                    state = "ok",
                    latency = 0L,
                    detail = $"运行中 · 端口 {Port} · WS客户端 {_clients.Count}",
                },
            };
            return Results.Json(new { ok = true, generatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(), results }, JsonWeb);
        });

        // ---- cookie auto-report (userscript): normalize not ported yet ----
        app.MapPost("/api/cookie", () => Results.Json(new
        {
            ok = false,
            hasCookie = _config.Cookie.Length > 0,
            cookie = "",
            count = 0,
            format = "",
            hasSession = false,
            hasJct = false,
            hasUid = false,
            uidDerived = false,
            missing = new { },
        }, JsonWeb));

        // ---- MAUI host bridge (paired with wwwroot/legacy/maui-bridge.js shim) ----
        app.MapPost("/api/maui/{**path}", async (HttpContext ctx, string? path) =>
        {
            var body = await ReadJsonObject(ctx);
            path ??= "";
            var page = MainPage.Current;
            void Ui(Action action) => page?.Dispatcher.Dispatch(() => { try { action(); } catch { } });

            switch (path)
            {
                case "version":
                    return Results.Json(new { app = VersionText, maui = true }, JsonWeb);
                case "app/quit":
                    Ui(() => App.QuitForReal());
                    return Results.Json(new { ok = true }, JsonWeb);
                case "app/focus-panel":
                case "app/minimize-tray":
                    return Results.Json(new { ok = true }, JsonWeb);
                case "shell/open-data-folder":
                    Recorder.OpenInExplorer(AppConfig.DataDir);
                    return Results.Json(new { ok = true }, JsonWeb);
                case "shell/open-config-folder":
                    Recorder.OpenInExplorer(AppConfig.DataDir);
                    return Results.Json(new { ok = true }, JsonWeb);
                case "shell/open-path":
                {
                    var p = SafeStr(body["path"]);
                    if (p.Length == 0) return Results.Json(new { error = "缺少 path" }, JsonWeb, statusCode: 400);
                    Recorder.OpenInExplorer(p);
                    return Results.Json(new { ok = true }, JsonWeb);
                }
                case "shell/open-external":
                {
                    var url = SafeStr(body["url"]);
                    if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        return Results.Json(new { error = "仅支持 http/https 链接" }, JsonWeb, statusCode: 400);
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                        return Results.Json(new { ok = true }, JsonWeb);
                    }
                    catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
                }
                case "autolaunch/set":
                {
                    var on = body["enabled"] is JsonValue ov && ov.TryGetValue<bool>(out var ob) && ob;
                    AutoLaunchService.Set(on);
                    return Results.Json(new { ok = true, enabled = on }, JsonWeb);
                }
                case "autolaunch/get":
                    return Results.Json(new { enabled = AutoLaunchService.Get() }, JsonWeb);
                case "update/check":
                    return Results.Json(await _updateChecker.CheckAsync(ctx.RequestAborted), JsonWeb);
                case "update/open-page":
                {
                    var url = "https://github.com/luoyunxiaotian/bili-live-tool/releases/latest";
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                        return Results.Json(new { ok = true }, JsonWeb);
                    }
                    catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
                }
                case "update/download-install":
                    return Results.Json(new { error = "MAUI 版暂无安装包，请到 Release 页手动下载" }, JsonWeb);
                case "bili/show":
                {
                    var rid = SafeStr(body["roomId"]).Trim();
                    var url = Regex.IsMatch(rid, "^\\d+$") ? $"https://live.bilibili.com/{rid}" : "https://live.bilibili.com/";
                    Ui(() => page?.ShowBiliBrowser(url));
                    return Results.Json(new { visible = true, url }, JsonWeb);
                }
                case "bili/show-login":
                    Ui(() => page?.ShowBiliBrowser("https://passport.bilibili.com/login"));
                    return Results.Json(new { visible = true }, JsonWeb);
                case "bili/hide":
                    Ui(() => page?.HideBiliBrowser());
                    return Results.Json(new { visible = false }, JsonWeb);
                case "bili/close":
                    Ui(() => page?.CloseBiliBrowser());
                    return Results.Json(new { visible = false }, JsonWeb);
                case "bili/refresh":
                    Ui(() => page?.ReloadBili());
                    return Results.Json(new { ok = true }, JsonWeb);
                case "bili/back":
                    Ui(() => page?.GoBackBili());
                    return Results.Json(new { ok = true }, JsonWeb);
                case "bili/forward":
                    Ui(() => page?.GoForwardBili());
                    return Results.Json(new { ok = true }, JsonWeb);
                case "bili/state":
                    return Results.Json(new
                    {
                        visible = page?.IsBiliVisible ?? false,
                        url = page?.BiliCurrentUrl ?? "",
                        roomId = _config.RoomId,
                    }, JsonWeb);
                case "bili/capture-cookie":
                    return Results.Json(new { ok = true, hasCookie = _config.Cookie.Length > 0, cookie = "" }, JsonWeb);
                case "bili/clear-cookies":
                    _config.SetBiliCookie("", "");
                    return Results.Json(new { ok = true }, JsonWeb);
                case "music/login":
                    _musicLogin.Show(SafeStr(body["platform"]));
                    return Results.Json(new { ok = true }, JsonWeb);
                case "music/capture":
                    return Results.Json(new { ok = true, hasCookie = _musicLogin.HasSavedCookie(SafeStr(body["platform"])) }, JsonWeb);
                case "music/has":
                    return Results.Json(new { has = _musicLogin.HasSavedCookie(SafeStr(body["platform"])) }, JsonWeb);
                case "music/clear":
                    _musicLogin.ClearCookies(SafeStr(body["platform"]));
                    return Results.Json(new { ok = true }, JsonWeb);
                case "tts/moss/ensure":
                    return Results.Json(_tts.EnsureMoss(), JsonWeb);
                case "tts/moss/stop":
                    return Results.Json(_tts.StopMoss(), JsonWeb);
                case "tts/moss/restart":
                    return Results.Json(await _tts.RestartMossAsync(), JsonWeb);
                case "tts/moss/status":
                    return Results.Json(_tts.MossStatus(), JsonWeb);
                case "server/start":
                case "server/stop":
                case "server/restart":
                    return Results.Json(new { ok = true, note = "服务内嵌于 MAUI 应用，随应用启停" }, JsonWeb);
                case "server/status":
                    return Results.Json(new { ok = true, running = IsRunning, port = Port }, JsonWeb);
                case "server/readlog":
                    return Results.Json(new { ok = true, lines = Array.Empty<string>() }, JsonWeb);
                case "debug/tray":
                    return Results.Json(new { tray = TrayService.LastDebug, titleBar = App.TitleBarDebug }, JsonWeb);
                case "keyview/start":
                    _keyview.Start();
                    return Results.Json(new { ok = true, overlayUrl = $"http://127.0.0.1:{Port}/keyview/overlay.html" }, JsonWeb);
                case "keyview/stop":
                    _keyview.Stop();
                    return Results.Json(new { ok = true }, JsonWeb);
                case "keyview/status":
                    return Results.Json(_keyview.Status(), JsonWeb);
                case "keyview/config-get":
                    return Results.Json(_keyview.Config.GetAll(), JsonWeb);
                case "keyview/config-set":
                {
                    // Accepts {config:{...}} (full replace, panel setAll) or {key,value}.
                    if (body["config"] is JsonObject cfgObj)
                    {
                        _keyview.Config.SetAll(cfgObj);
                        return Results.Json(new { ok = true }, JsonWeb);
                    }
                    var key = SafeStr(body["key"]);
                    if (key.Length > 0)
                    {
                        _keyview.Config.Set(key, body["value"]?.DeepClone());
                        return Results.Json(new { ok = true }, JsonWeb);
                    }
                    return Results.Json(new { error = "缺少 config 或 key/value" }, JsonWeb, statusCode: 400);
                }
                case "keyview/open-overlay":
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                            $"http://127.0.0.1:{Port}/keyview/overlay.html") { UseShellExecute = true });
                        return Results.Json(new { ok = true }, JsonWeb);
                    }
                    catch (Exception e) { return Results.Json(new { error = e.Message }, JsonWeb, statusCode: 500); }
                }
                case "keyview/themes":
                {
                    try
                    {
                        var manifest = Path.Combine(AppConfig.LegacyRoot, "keyview", "themes", "themes.manifest.json");
                        var content = File.Exists(manifest) ? JsonNode.Parse(File.ReadAllText(manifest)) : new JsonArray();
                        return Results.Json(content, JsonWeb);
                    }
                    catch { return Results.Json(new JsonArray(), JsonWeb); }
                }
                default:
                    return Results.Json(new { error = "未知 MAUI 桥接调用: " + path }, JsonWeb, statusCode: 404);
            }
        });

        // ---- TTS reverse proxy to the standalone engines (8020/8021) ----
        app.Map("/api/tts/{engine}/{**rest}", async (HttpContext ctx, string engine, string? rest) =>
        {
            var port = engine == "moss" ? TtsHost.MossPort : engine == "edge" ? TtsHost.EdgePort : 0;
            if (port == 0) return Results.Json(new { error = "未知引擎: " + engine }, JsonWeb, statusCode: 404);
            var suffix = (rest ?? "") + (ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : "");
            var target = new Uri($"http://127.0.0.1:{port}/{suffix}");
            try
            {
                using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
                if (HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method))
                {
                    ctx.Request.EnableBuffering();
                    using var ms = new MemoryStream();
                    await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
                    var bytes = ms.ToArray();
                    req.Content = new ByteArrayContent(bytes);
                    req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                        string.IsNullOrEmpty(ctx.Request.ContentType) ? "application/json" : ctx.Request.ContentType);
                }
                using var resp = await _proxy.SendAsync(req, ctx.RequestAborted);
                ctx.Response.StatusCode = (int)resp.StatusCode;
                var contentType = resp.Content.Headers.ContentType?.ToString();
                if (!string.IsNullOrEmpty(contentType)) ctx.Response.ContentType = contentType;
                await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                return Results.Empty;
            }
            catch
            {
                return Results.Json(
                    new { error = engine == "moss" ? "MOSS 引擎未运行" : "edge-tts 服务未运行（请检查 tts 目录或系统语音）" },
                    JsonWeb, statusCode: 502);
            }
        });

        // Graceful JSON fallback so the panel never sees HTML 404s.
        app.Map("/api/{**path}", (HttpContext ctx) =>
        {
            var ok = HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method);
            return ok
                ? Results.Json(new { }, JsonWeb)
                : Results.Json(new { error = "该功能尚未移植到 MAUI demo" }, JsonWeb);
        });
    }

    // ---------------- WS ----------------

    private void MapWs(WebApplication app)
    {
        app.MapGet("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var id = Guid.NewGuid();
            _clients[id] = ws;
            try
            {
                var buf = new byte[4096];
                while (ws.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested)
                {
                    var res = await ws.ReceiveAsync(buf, ctx.RequestAborted);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { }
            finally
            {
                _clients.TryRemove(id, out _);
                try { ws.Dispose(); } catch { }
            }
        });
    }

    // ---------------- helpers ----------------

    private async Task<JsonObject> ReadJsonObject(HttpContext ctx)
    {
        try
        {
            var body = await ctx.Request.ReadFromJsonAsync<JsonObject>(JsonWeb);
            return body ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static int ParseInt(string? s, int def) => int.TryParse(s, out var v) ? v : def;

    private void ApplyRecordingFlags()
    {
        var rec = _config.GetNode("recording") as JsonObject;
        if (rec == null) return;
        foreach (var t in Recorder.Types)
            if (rec.TryGetPropertyValue(t, out var v) && v is JsonValue val && val.TryGetValue<bool>(out var b))
                _recorder.SetEnabled(t, b);
    }

    private object StatusPayload()
    {
        var st = _hub.LastStatus;
        var (recording, folders) = _config.RecordingAndFolders();
        return new
        {
            state = st.State,
            roomId = _config.RoomId,
            realRoomId = st.RealRoomId,
            uid = st.AnchorUid,
            popularity = st.Popularity,
            connectedAt = st.ConnectedAt,
            error = st.Error,
            title = st.Title,
            recording,
            folders,
        };
    }

    private static string Clip(string s, int max, string def)
    {
        s ??= "";
        return s.Length > max ? s[..max] : (s.Length == 0 ? def : s);
    }

    private static string SafeStr(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? "" : "";

    private static double SafeD(JsonNode? n, double def)
        => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : def;

    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

    private static string InSet(string v, params string[] set) => set.Contains(v) ? v : set[0];

    private static string HexColor(string v, string def)
        => Regex.IsMatch(v, "^#[0-9a-fA-F]{3,8}$") ? v : def;

    // Port of the mock-event generator in server.js (incl. interact/follow).
    private LiveEvent? BuildMockEvent(string type, JsonObject? b)
    {
        var now = DateTime.Now;
        string time = now.ToString("yyyy-MM-dd HH:mm:ss");
        long ts = new DateTimeOffset(now, TimeZoneInfo.Local.GetUtcOffset(now)).ToUnixTimeMilliseconds();
        var uname = SafeStr(b?["uname"]);
        if (uname.Length == 0) uname = "测试用户" + _rand.Next(1000, 9999);
        var uid = SafeStr(b?["uid"]);
        if (uid.Length == 0) uid = _rand.Next(100000, 999999).ToString();

        switch (type)
        {
            case "danmu":
            {
                var msg = SafeStr(b?["msg"]);
                return new LiveEvent
                {
                    Type = "danmu", Time = time, Ts = ts, Uid = uid, Uname = uname,
                    Msg = msg.Length > 0 ? msg : "这是一条测试弹幕",
                };
            }
            case "gifts":
            {
                var num = Math.Max(1, b?["num"]?.GetValue<long?>() ?? 1);
                var price = b?["price"]?.GetValue<long?>() ?? 100;
                var totalCoin = num * price;
                var gift = SafeStr(b?["giftName"]);
                return new LiveEvent
                {
                    Type = "gifts", Time = time, Ts = ts, Uid = uid, Uname = uname,
                    GiftName = gift.Length > 0 ? gift : "小心心", Num = (int)num, Price = price,
                    TotalCoin = totalCoin, CoinType = "gold",
                    Value = Math.Round(totalCoin / 1000.0 * 100) / 100,
                };
            }
            case "guard":
            {
                var level = Math.Max(1, b?["guardLevel"]?.GetValue<long?>() ?? 3);
                var names = new[] { "", "总督", "提督", "舰长" };
                var name = level <= 3 ? names[level] : "舰长";
                return new LiveEvent
                {
                    Type = "guard", Time = time, Ts = ts, Uid = uid, Uname = uname,
                    Level = (int)level, LevelName = name, Num = 1, Price = 198000,
                    TotalCoin = 198000, CoinType = "gold", Value = 198,
                    IsGuard = true, GuardLevel = (int)level,
                };
            }
            case "superchat":
            {
                var msg = SafeStr(b?["msg"]);
                return new LiveEvent
                {
                    Type = "superchat", Time = time, Ts = ts, Uid = uid, Uname = uname,
                    Msg = msg.Length > 0 ? msg : "这是一条测试醒目留言",
                    Price = b?["price"]?.GetValue<long?>() ?? 30, StartTime = ts, EndTime = ts + 60000,
                };
            }
            case "follow":
                return new LiveEvent { Type = "interact", Time = time, Ts = ts, Uid = uid, Uname = uname, MsgType = 2 };
            case "interact":
            case "enter":
                return new LiveEvent { Type = "interact", Time = time, Ts = ts, Uid = uid, Uname = uname, MsgType = 1 };
            default:
                return null;
        }
    }

    private void OnHubEvent(LiveEvent ev)
        => BroadcastJson(JsonSerializer.Serialize(new { type = "event", data = ev }, JsonWeb));

    private void OnHubStatus(LiveStatusInfo st)
        => BroadcastJson(JsonSerializer.Serialize(new { type = "status", data = StatusPayload() }, JsonWeb));

    private void OnHubOutbound(string type, object? data)
        => BroadcastJson(JsonSerializer.Serialize(new { type, data }, JsonWeb));

    private void BroadcastJson(string json)
    {
        foreach (var kv in _clients)
        {
            var ws = kv.Value;
            if (ws.State != WebSocketState.Open) continue;
            _ = SendSafeAsync(ws, json);
        }
    }

    // KeyView overlay clients (root-path WS); separate from the live panel WS
    // so overlays never receive live events.
    private void BroadcastKeyViewJson(string json)
    {
        foreach (var kv in _keyviewClients)
        {
            var ws = kv.Value;
            if (ws.State != WebSocketState.Open) continue;
            _ = SendSafeAsync(ws, json);
        }
    }

    private static async Task SendKeyView(WebSocket ws, string json)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text, true, cts.Token);
        }
        catch { }
    }

    private static async Task SendSafeAsync(WebSocket ws, string json)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text, true, cts.Token);
        }
        catch { }
    }

    public async Task StopAsync()
    {
        _hub.OnEvent -= OnHubEvent;
        _hub.StatusChanged -= OnHubStatus;
        _hub.OnOutbound -= OnHubOutbound;
        _keyview.OnEventJson -= BroadcastKeyViewJson;
        _keyview.OnConfigFrame -= BroadcastKeyViewJson;
        foreach (var kv in _clients)
        {
            try { await kv.Value.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { }
        }
        _clients.Clear();
        if (_app != null)
        {
            try
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(stopCts.Token);
            }
            catch { }
            await _app.DisposeAsync();
            _app = null;
        }
        IsRunning = false;
    }

    private sealed record ConnectBody(string? RoomId, string? Cookie);
}
