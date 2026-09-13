using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiLi_live_Tool.Services;

public sealed record DanmuInfo(string Token, List<DanmuHost> Hosts);
public sealed record DanmuHost(string Host, int WssPort, int Port);
public sealed record RoomInfo(long RealRoomId, long Uid, long ShortId);
public sealed record RoomLiveStatus(int LiveStatus, string Title);
public sealed record GuardTopItem(string Name, long Accompany, int GuardLevel);
public sealed record RoomLiveInfo(long Online, long GuardTotal, long[] GuardCount, List<GuardTopItem> Top3);

/// <summary>
/// Port of the REST part of Bin/lib/bili.js: room resolution, live status,
/// danmu server info with WBI signing (w_rid/wts).
/// </summary>
public static partial class BiliApi
{
    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = new();
    private static readonly long[] MixinKeyEncTab =
    {
        46,47,18,2,53,8,23,32,15,50,10,31,58,3,45,35,27,43,5,49,33,9,42,19,29,28,14,39,12,38,41,13,
        37,48,7,16,24,55,40,61,26,17,0,1,60,51,30,4,22,25,54,21,56,59,6,63,57,62,11,36,20,34,44,52
    };

    private static (string ImgKey, string SubKey)? _wbiCache;
    private static DateTime _wbiCacheUtc;

    private static readonly Regex FilterCharsRegex = new("[!'()*]", RegexOptions.Compiled);

    private static async Task<JsonElement> FetchJsonAsync(string url, string? cookie, CancellationToken ct, int timeoutMs = 10000)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);
        req.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
        req.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        if (!string.IsNullOrEmpty(cookie)) req.Headers.TryAddWithoutValidation("Cookie", cookie);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var resp = await Http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
        return doc.RootElement.Clone();
    }

    private static string ApiError(JsonElement j)
    {
        if (j.ValueKind == JsonValueKind.Object)
        {
            if (j.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString() ?? "";
            if (j.TryGetProperty("code", out var c)) return "code=" + c.ToString();
        }
        return "unknown response";
    }

    private static long GetLong(JsonElement el, string name, long def = 0)
    {
        if (el.ValueKind != JsonValueKind.Object) return def;
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : def;
    }

    private static string GetStr(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return "";
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    public static async Task<RoomInfo> GetRealRoomIdAsync(string roomId, CancellationToken ct)
    {
        var url = "https://api.live.bilibili.com/room/v1/Room/room_init?id=" + Uri.EscapeDataString(roomId);
        var j = await FetchJsonAsync(url, null, ct);
        if (j.ValueKind != JsonValueKind.Object || GetLong(j, "code") != 0 || !j.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
            throw new Exception("获取房间信息失败: " + ApiError(j));
        return new RoomInfo(GetLong(d, "room_id"), GetLong(d, "uid"), GetLong(d, "short_id"));
    }

    public static async Task<RoomLiveStatus> GetRoomLiveStatusAsync(long realRoomId, string? cookie, CancellationToken ct)
    {
        var url = "https://api.live.bilibili.com/room/v1/Room/get_info?room_id=" + realRoomId;
        var j = await FetchJsonAsync(url, cookie, ct);
        if (j.ValueKind != JsonValueKind.Object || GetLong(j, "code") != 0 || !j.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
            throw new Exception("获取房间状态失败: " + ApiError(j));
        return new RoomLiveStatus((int)GetLong(d, "live_status"), GetStr(d, "title"));
    }

    // ----- WBI signing (port of encWbi/getWbiKeys in bili.js) -----

    private static string GetMixinKey(string orig)
    {
        var chars = new char[32];
        for (int i = 0; i < 32; i++) chars[i] = orig[(int)MixinKeyEncTab[i]];
        return new string(chars);
    }

    private static async Task<(string ImgKey, string SubKey)> GetWbiKeysAsync(CancellationToken ct)
    {
        if (_wbiCache != null && DateTime.UtcNow - _wbiCacheUtc < TimeSpan.FromMinutes(30)) return _wbiCache.Value;
        var j = await FetchJsonAsync("https://api.bilibili.com/x/web-interface/nav", null, ct);
        if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
            throw new Exception("获取WBI密钥失败: " + ApiError(j));
        if (!d.TryGetProperty("wbi_img", out var wbi) || wbi.ValueKind != JsonValueKind.Object)
            throw new Exception("获取WBI密钥失败: 缺少 wbi_img");
        var imgUrl = GetStr(wbi, "img_url");
        var subUrl = GetStr(wbi, "sub_url");
        if (imgUrl.Length == 0 || subUrl.Length == 0) throw new Exception("获取WBI密钥失败: 空 key");
        var imgKey = KeyFromUrl(imgUrl);
        var subKey = KeyFromUrl(subUrl);
        _wbiCache = (imgKey, subKey);
        _wbiCacheUtc = DateTime.UtcNow;
        return _wbiCache.Value;
    }

    private static string KeyFromUrl(string url)
    {
        var slash = url.LastIndexOf('/');
        var dot = url.LastIndexOf('.');
        if (dot < 0) dot = url.Length;
        return url.Substring(slash + 1, Math.Max(0, dot - slash - 1));
    }

    private static async Task<string> EncWbiAsync(Dictionary<string, string> parameters, CancellationToken ct)
    {
        var (imgKey, subKey) = await GetWbiKeysAsync(ct);
        var mixinKey = GetMixinKey(imgKey + subKey);
        parameters["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var query = string.Join("&", parameters
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(FilterCharsRegex.Replace(p.Value, ""))));
        var wRid = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey)));
        return query + "&w_rid=" + wRid;
    }

    public static async Task<DanmuInfo> GetDanmuInfoAsync(long realRoomId, string? cookie, CancellationToken ct)
    {
        var url = $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?id={realRoomId}";
        // Try WBI-signed URL first; fall back to unsigned on key fetch failure (same as bili.js).
        try
        {
            var qs = await EncWbiAsync(new Dictionary<string, string> { ["id"] = realRoomId.ToString() }, ct);
            url = "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?" + qs;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall back to unsigned */ }

        var j = await FetchJsonAsync(url, cookie, ct);
        if (j.ValueKind != JsonValueKind.Object || GetLong(j, "code") != 0 || !j.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
            throw new Exception("获取弹幕服务器失败: " + ApiError(j));

        var token = GetStr(d, "token");
        var hosts = new List<DanmuHost>();
        if (d.TryGetProperty("host_list", out var hl) && hl.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in hl.EnumerateArray())
            {
                var host = GetStr(h, "host");
                if (host.Length == 0) continue;
                hosts.Add(new DanmuHost(host, (int)GetLong(h, "wss_port", 443), (int)GetLong(h, "port", 443)));
            }
        }
        return new DanmuInfo(token, hosts);
    }

    /// <summary>Port of getRoomLiveInfo in bili.js: online count + guard totals + top3.</summary>
    public static async Task<RoomLiveInfo> GetRoomLiveInfoAsync(long roomId, long anchorUid, CancellationToken ct)
    {
        long online = 0, guardTotal = 0;
        var guardCount = new long[3];
        var top3 = new List<GuardTopItem>();
        try
        {
            var info = await FetchJsonAsync($"https://api.live.bilibili.com/xlive/web-room/v1/index/get_info_by_room?room_id={roomId}", null, ct);
            if (info.ValueKind == JsonValueKind.Object && GetLong(info, "code") == 0 &&
                info.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                d.TryGetProperty("room_info", out var ri) && ri.ValueKind == JsonValueKind.Object &&
                ri.TryGetProperty("online", out var onEl) && onEl.ValueKind == JsonValueKind.Number)
                online = onEl.GetInt64();
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        try
        {
            var url = $"https://api.live.bilibili.com/xlive/app-room/v2/guardTab/topListNew?roomid={roomId}&ruid={anchorUid}&page=1&page_size=30";
            var g = await FetchJsonAsync(url, null, ct);
            if (g.ValueKind == JsonValueKind.Object && GetLong(g, "code") == 0 && g.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                if (d.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
                    guardTotal = GetLong(infoEl, "num");
                var all = new List<JsonElement>();
                if (d.TryGetProperty("top3", out var t3) && t3.ValueKind == JsonValueKind.Array)
                    all.AddRange(t3.EnumerateArray());
                if (d.TryGetProperty("list", out var ls) && ls.ValueKind == JsonValueKind.Array)
                    all.AddRange(ls.EnumerateArray());
                foreach (var item in all)
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("uinfo", out var uinfo) || uinfo.ValueKind != JsonValueKind.Object) continue;
                    var level = 0;
                    if (uinfo.TryGetProperty("guard", out var guardEl) && guardEl.ValueKind == JsonValueKind.Object &&
                        guardEl.TryGetProperty("level", out var lvEl) && lvEl.ValueKind == JsonValueKind.Number)
                        level = lvEl.GetInt32();
                    if (level >= 1 && level <= 3) guardCount[level - 1]++;
                }
                if (d.TryGetProperty("top3", out var topEl) && topEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in topEl.EnumerateArray())
                    {
                        string name = "";
                        long accompany = 0;
                        var level = 0;
                        if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("uinfo", out var ui) && ui.ValueKind == JsonValueKind.Object)
                        {
                            if (ui.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.Object && b.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                                name = nEl.GetString() ?? "";
                            if (ui.TryGetProperty("guard", out var ge) && ge.ValueKind == JsonValueKind.Object &&
                                ge.TryGetProperty("level", out var lv2) && lv2.ValueKind == JsonValueKind.Number)
                                level = lv2.GetInt32();
                        }
                        if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("accompany", out var ac) && ac.ValueKind == JsonValueKind.Number)
                            accompany = ac.GetInt64();
                        top3.Add(new GuardTopItem(name, accompany, level));
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return new RoomLiveInfo(online, guardTotal, guardCount, top3);
    }

    // ----- Write operations & extra queries (port of sendDanmu/like/blindbox/stats in bili.js) -----

    private static string? CookieValue(string cookie, string name)
    {
        if (string.IsNullOrEmpty(cookie)) return null;
        var m = Regex.Match(cookie, "(?:^|;\\s*)" + name + "=([^;]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Send a danmu (needs bili_jct CSRF in cookie). Port of sendDanmu in bili.js.</summary>
    public static async Task SendDanmuAsync(long roomId, string msg, string cookie, CancellationToken ct)
    {
        var csrf = CookieValue(cookie ?? "", "bili_jct");
        if (string.IsNullOrEmpty(csrf)) throw new Exception("缺少 bili_jct(CSRF)，无法发送弹幕（请先导入登录Cookie）");
        var raw = msg ?? "";
        var clipped = raw.Length > 60 ? raw[..60] : raw;
        var text = Regex.Replace(clipped, "[<>&]", "");
        if (text.Length == 0) throw new Exception("弹幕内容为空");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.live.bilibili.com/msg/send")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["bubble"] = "0", ["color"] = "16777215", ["danmaku"] = text, ["fontsize"] = "25", ["mode"] = "1",
                ["msg"] = text, ["roomid"] = roomId.ToString(), ["rnd"] = Random.Shared.Next().ToString(),
                ["csrf"] = csrf, ["csrf_token"] = csrf,
            })
        };
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);
        req.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
        req.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
        if (!string.IsNullOrEmpty(cookie)) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(10000);
        using var resp = await Http.SendAsync(req, cts.Token);
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
        var j = doc.RootElement;
        if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("code", out var code) && code.GetInt64() != 0)
        {
            var message = j.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "code=" + code;
            throw new Exception("发送弹幕失败: " + message);
        }
    }

    public sealed record UserInfo(bool IsLogin, long Uid, string Uname);

    /// <summary>Current login user via nav API (uid works with SESSDATA only). Port of getUserInfo.</summary>
    public static async Task<UserInfo> GetUserInfoAsync(string? cookie, CancellationToken ct)
    {
        var j = await FetchJsonAsync("https://api.bilibili.com/x/web-interface/nav", cookie, ct, 8000);
        if (j.ValueKind != JsonValueKind.Object) return new UserInfo(false, 0, "");
        if (!j.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return new UserInfo(false, 0, "");
        var isLogin = d.TryGetProperty("isLogin", out var il) && il.ValueKind == JsonValueKind.True;
        long uid = 0;
        if (d.TryGetProperty("mid", out var mid) && mid.ValueKind == JsonValueKind.Number) uid = mid.GetInt64();
        var uname = d.TryGetProperty("uname", out var un) && un.ValueKind == JsonValueKind.String ? un.GetString() ?? "" : "";
        return new UserInfo(isLogin, uid, uname);
    }

    public sealed record LikeContext(long RoomId, long Uid, long AnchorId, string ImgKey, string SubKey, string Csrf);

    /// <summary>Like context: uid/anchorId/wbiKeys/csrf (port of initLikeContext). Cache once per like session.</summary>
    public static async Task<LikeContext> InitLikeContextAsync(long roomId, string cookie, CancellationToken ct)
    {
        var csrf = CookieValue(cookie ?? "", "bili_jct") ?? throw new Exception("缺少 bili_jct(CSRF)，无法点赞（请先导入登录Cookie）");
        var user = await GetUserInfoAsync(cookie, ct);
        if (user.Uid == 0) throw new Exception("无法获取用户uid，请检查Cookie是否有效");
        var room = await GetRealRoomIdAsync(roomId.ToString(), ct);
        if (room.Uid == 0) throw new Exception("无法获取主播uid");
        var (imgKey, subKey) = await GetWbiKeysAsync(ct);
        return new LikeContext(roomId, user.Uid, room.Uid, imgKey, subKey, csrf);
    }

    /// <summary>One like click with a prebuilt context; returns raw (code, message) — caller handles -352 retry.</summary>
    public static async Task<(long Code, string Message)> LikeOnceAsync(LikeContext ctx, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["click_time"] = "1",
            ["room_id"] = ctx.RoomId.ToString(),
            ["uid"] = ctx.Uid.ToString(),
            ["anchor_id"] = ctx.AnchorId.ToString(),
            ["web_location"] = "444.8",
            ["csrf"] = ctx.Csrf,
        };
        var query = await EncWbiAsync(parameters, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.live.bilibili.com/xlive/app-ucenter/v1/like_info_v3/like/likeReportV3?" + query);
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);
        req.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
        req.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(10000);
        using var resp = await Http.SendAsync(req, cts.Token);
        var txt = await resp.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(txt);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var code))
        {
            var message = doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "code=" + code;
            return (code.GetInt64(), message);
        }
        return (-1, "非JSON响应");
    }

    /// <summary>One-shot like (context rebuilt every call) — convenience wrapper.</summary>
    public static async Task<(long Code, string Message)> LikeRoomAsync(long roomId, string cookie, CancellationToken ct)
        => await LikeOnceAsync(await InitLikeContextAsync(roomId, cookie, ct), ct);

    public sealed record RoomGift(long Id, string Name, long Price, string CoinType);

    /// <summary>Room gift panel list (no cookie needed). Port of getRoomGiftList.</summary>
    public static async Task<List<RoomGift>> GetRoomGiftListAsync(long roomId, CancellationToken ct)
    {
        var url = $"https://api.live.bilibili.com/xlive/web-room/v1/giftPanel/roomGiftList?platform=pc&room_id={roomId}";
        var j = await FetchJsonAsync(url, null, ct);
        var list = new List<RoomGift>();
        if (j.ValueKind != JsonValueKind.Object || GetLong(j, "code") != 0 || !j.TryGetProperty("data", out var d)) return list;
        if (!d.TryGetProperty("gift_config", out var gc) || gc.ValueKind != JsonValueKind.Object) return list;
        if (!gc.TryGetProperty("base_config", out var bc) || bc.ValueKind != JsonValueKind.Object) return list;
        if (!bc.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var g in arr.EnumerateArray())
        {
            list.Add(new RoomGift(GetLong(g, "id"), GetStr(g, "name"), GetLong(g, "price"), GetStr(g, "coin_type")));
        }
        return list;
    }

    public sealed record BlindBoxInfo(long GiftId, string Name, double Cost, double ExpectIncome, List<BlindBoxGift> Gifts);
    public sealed record BlindBoxGift(string Name, long Price, double Chance);

    /// <summary>Blind-box prize pool. Port of getBlindBoxInfo.</summary>
    public static async Task<BlindBoxInfo?> GetBlindBoxInfoAsync(long giftId, string? cookie, CancellationToken ct)
    {
        var url = $"https://api.live.bilibili.com/xlive/general-interface/v1/blindFirstWin/getInfo?gift_id={giftId}";
        var j = await FetchJsonAsync(url, cookie, ct);
        if (j.ValueKind != JsonValueKind.Object || GetLong(j, "code") != 0 || !j.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
            return null;
        var gifts = new List<BlindBoxGift>();
        double expect = 0;
        if (d.TryGetProperty("gifts", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in arr.EnumerateArray())
            {
                var name = GetStr(g, "gift_name");
                var price = GetLong(g, "price");
                var chance = 0.0;
                if (g.TryGetProperty("chance", out var ch))
                {
                    var raw = ch.ValueKind == JsonValueKind.Number ? ch.GetDouble().ToString("0.############", System.Globalization.CultureInfo.InvariantCulture) : ch.ToString();
                    double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out chance);
                }
                gifts.Add(new BlindBoxGift(name, price, chance));
                expect += price * chance / 100.0;
            }
        }
        expect = Math.Round(expect / 1000 * 100) / 100;
        return new BlindBoxInfo(
            giftId,
            GetStr(d, "blind_gift_name"),
            Math.Round(GetLong(d, "blind_price") / 1000.0 * 100) / 100,
            expect,
            gifts);
    }

    /// <summary>Auto-detect blind boxes by keyword scan of the gift list. Port of autoDetectBlindBoxes.</summary>
    public static async Task<List<BlindBoxInfo>> AutoDetectBlindBoxesAsync(long roomId, string? cookie, CancellationToken ct)
    {
        var keyword = new Regex("盲盒|魔盒|扭蛋|神秘|宝盒", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var result = new List<BlindBoxInfo>();
        foreach (var g in await GetRoomGiftListAsync(roomId, ct))
        {
            if (!keyword.IsMatch(g.Name)) continue;
            try
            {
                var info = await GetBlindBoxInfoAsync(g.Id, cookie, ct);
                if (info != null) result.Add(info);
            }
            catch { /* skip unreachable gift pools */ }
        }
        return result;
    }

    public sealed record RoomStats(long RoomId, long Online, long Popularity, long Follower, long Guard, string Title, string Uname);

    /// <summary>Room online/follower/guard stats. Port of getRoomStats.</summary>
    public static async Task<RoomStats> GetRoomStatsAsync(long roomId, string? cookie, CancellationToken ct)
    {
        long online = 0, popularity = 0, follower = 0, guard = 0;
        string title = "", uname = "";
        try
        {
            var info = await FetchJsonAsync($"https://api.live.bilibili.com/room/v1/Room/get_info?room_id={roomId}", cookie, ct);
            if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                online = GetLong(d, "online");
                popularity = GetLong(d, "popularity") != 0 ? GetLong(d, "popularity") : online;
                title = GetStr(d, "title");
            }
        }
        catch { }
        try
        {
            var br = await FetchJsonAsync($"https://api.live.bilibili.com/xlive/web-room/v1/index/get_info_by_room?room_id={roomId}", cookie, ct);
            if (br.ValueKind == JsonValueKind.Object && br.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                if (d.TryGetProperty("anchor_info", out var ai) && ai.ValueKind == JsonValueKind.Object &&
                    ai.TryGetProperty("base_info", out var bi) && bi.ValueKind == JsonValueKind.Object)
                {
                    follower = GetLong(bi, "follower_num");
                    uname = GetStr(bi, "uname");
                }
                if (d.TryGetProperty("room_info", out var ri) && ri.ValueKind == JsonValueKind.Object &&
                    ri.TryGetProperty("online", out var onl) && onl.ValueKind == JsonValueKind.Number)
                    online = onl.GetInt64();
            }
        }
        catch { }
        try
        {
            var g = await FetchJsonAsync($"https://api.live.bilibili.com/xlive/web-room/v1/index/get_room_guard_info?roomid={roomId}", cookie, ct);
            if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                d.TryGetProperty("info", out var gi) && gi.ValueKind == JsonValueKind.Array)
                guard = gi.GetArrayLength();
        }
        catch { }
        return new RoomStats(roomId, online, popularity, follower, guard, title, uname);
    }
}
