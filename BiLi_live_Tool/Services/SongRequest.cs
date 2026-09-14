using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BiLi_live_Tool.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Port of lib/music-api.js: QQ / netease / kugou / bilibili search + play URLs.
// ─────────────────────────────────────────────────────────────────────────────
public static partial class MusicApi
{
    public sealed record Song(
        string Id, string Name, string Artist, string Album, string Platform, bool Vip,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mid = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hash = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Bvid = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Duration = null);

    public sealed record PlayUrl(string Url, bool Vip);

    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    private static async Task<JsonNode?> FetchJsonAsync(string url, HttpMethod? method, JsonObject? body, Dictionary<string, string>? headers, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (headers != null)
            foreach (var (k, v) in headers)
                req.Headers.TryAddWithoutValidation(k, v);
        if (body != null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        try { return JsonNode.Parse(text); } catch { return JsonValue.Create(text); }
    }

    private static string Safe(JsonNode? n)
    {
        if (n is not JsonValue v) return "";
        if (v.TryGetValue<string>(out var s)) return (s ?? "").Trim();
        // Several upstream fields are JSON numbers (netease song id, kugou candidate id).
        if (v.TryGetValue<long>(out var l)) return l.ToString();
        if (v.TryGetValue<double>(out var d)) return d.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
        return v.ToJsonString().Trim('"');
    }

    private static async Task<PlayUrl> GetRedirectUrlAsync(string url, CancellationToken ct)
    {
        // HttpClient follows redirects; the final URL is on the response request.
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)resp.StatusCode == 200)
            return new PlayUrl(resp.RequestMessage?.RequestUri?.ToString() ?? url, false);
        throw new Exception("HTTP " + (int)resp.StatusCode);
    }

    // ─── QQ ───

    /// <summary>把 JSON 的布尔/数字/字符串统一读成 0/1 标志（QQ 的 pay 字段实际是数字）。</summary>
    private static long ToFlag(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        if (v.TryGetValue<string>(out var s2) && long.TryParse(s2, out var p)) return p;
        return 0;
    }

    public static async Task<List<Song>> QqSearchAsync(string keyword, int limit, string searchType, CancellationToken ct)
    {
        var st = searchType == "lyric" ? 7 : 0;
        var base0 = "https://c.y.qq.com/soso/fcgi-bin/search_for_qq_cp?w=" + Uri.EscapeDataString(keyword) +
                    "&n=" + limit + "&p=1&format=json&remoteplace=txt.yqq.top&new_json=1&cr=1&catZhida=1&t=";
        var headers = new Dictionary<string, string> { ["User-Agent"] = Ua, ["Referer"] = "https://y.qq.com/" };

        List<Song> Map(JsonNode? j)
        {
            var list = new List<Song>();
            var arr = j?["data"]?["song"]?["list"] as JsonArray;
            foreach (var s in arr ?? new JsonArray())
            {
                var artists = string.Join("/", ((s?["singer"] as JsonArray) ?? new JsonArray()).Select(x => Safe(x?["name"])));
                // QQ 的 pay.payplay / paydownload 是数字（0/1），旧代码按 bool 解析永远失败 → VIP 歌被标成免费
                bool vip = ToFlag(s?["pay"]?["payplay"]) > 0 || ToFlag(s?["pay"]?["paydownload"]) > 0 || ToFlag(s?["pay"]?["price_track"]) > 1;
                list.Add(new Song(
                    Safe(s?["songmid"]), Safe(s?["songname"]) is var n && n.Length > 0 ? n : Safe(s?["title"]),
                    artists, Safe(s?["albumname"]), "qq", vip, Mid: Safe(s?["songmid"])));
            }
            return list;
        }

        var outList = Map(await FetchJsonAsync(base0 + st, null, null, headers, ct));
        // Lyric search may be unsupported on the new endpoint; fall back to plain search.
        if (outList.Count == 0 && st != 0) outList = Map(await FetchJsonAsync(base0 + "0", null, null, headers, ct));
        return outList;
    }

    public static async Task<PlayUrl> QqGetSongUrlAsync(string songmid, string? cookie, CancellationToken ct)
    {
        var file = "M500" + songmid + songmid + ".mp3";
        var reqData = new JsonObject
        {
            ["req_1"] = new JsonObject
            {
                ["module"] = "vkey.GetVkeyServer",
                ["method"] = "CgiGetVkey",
                ["param"] = new JsonObject
                {
                    ["filename"] = new JsonArray(file),
                    ["guid"] = "10000",
                    ["songmid"] = new JsonArray(songmid),
                    ["songtype"] = new JsonArray(0),
                    ["uin"] = "0",
                    ["loginflag"] = 1,
                    ["platform"] = "20",
                },
            },
            ["loginUin"] = "0",
            ["comm"] = new JsonObject { ["uin"] = "0", ["format"] = "json", ["ct"] = 24, ["cv"] = 0 },
        };
        var headers = new Dictionary<string, string> { ["User-Agent"] = Ua, ["Referer"] = "https://y.qq.com/" };
        if (!string.IsNullOrEmpty(cookie)) headers["Cookie"] = cookie;
        var j = await FetchJsonAsync("https://u.y.qq.com/cgi-bin/musicu.fcg", HttpMethod.Post, reqData, headers, ct);
        var purl = Safe(j?["req_1"]?["data"]?["midurlinfo"]?[0]?["purl"]);
        if (purl.Length == 0) return new PlayUrl("", true);
        var sip = Safe(j?["req_1"]?["data"]?["sip"]?[0]);
        var playUrl = sip + purl;
        if (!playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) playUrl = "https://" + playUrl;
        return new PlayUrl(playUrl, false);
    }

    // ─── netease ───

    public static async Task<List<Song>> NeteaseSearchAsync(string keyword, int limit, string searchType, CancellationToken ct)
    {
        var nt = searchType == "artist" ? 100 : searchType == "lyric" ? 1006 : 1;
        var url = "https://music.163.com/api/search/get?s=" + Uri.EscapeDataString(keyword) + "&type=" + nt + "&offset=0&limit=" + limit;
        var j = await FetchJsonAsync(url, null, null, new Dictionary<string, string> { ["User-Agent"] = Ua, ["Referer"] = "https://music.163.com" }, ct);
        var list = new List<Song>();
        foreach (var s in (j?["result"]?["songs"] as JsonArray) ?? new JsonArray())
        {
            var artists = string.Join("/", ((s?["artists"] as JsonArray) ?? new JsonArray()).Select(x => Safe(x?["name"])));
            // Netease fee: 0 free, 1 VIP, 4 paid album, 8 free at low bitrate ("低音质免费").
            // Measured 2026-09-14 on 196 search hits: fee 1/4 never resolve without login
            // (0/144 playable, all land on music.163.com/404), fee 0/8 resolve to a playable
            // CDN mp3 (54/55) — so only 1/4 may be badged VIP. Anything unknown stays VIP.
            var neteaseFee = s?["fee"]?.GetValue<long?>() ?? 0;
            list.Add(new Song(
                Safe(s?["id"]), Safe(s?["name"]), artists, Safe(s?["album"]?["name"]), "netease",
                Vip: neteaseFee != 0 && neteaseFee != 8));
        }
        return list;
    }

    public static async Task<PlayUrl> NeteaseGetSongUrlAsync(string songId, string? cookie, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(cookie) && cookie.Contains("MUSIC_U="))
        {
            try
            {
                var url = "https://music.163.com/api/song/enhance/player/url/v1?ids=[" + songId + "]&level=standard&encodeType=mp3";
                var j = await FetchJsonAsync(url, null, null, new Dictionary<string, string> { ["User-Agent"] = Ua, ["Referer"] = "https://music.163.com", ["Cookie"] = cookie }, ct);
                var durl = Safe(j?["data"]?[0]?["url"]);
                if (durl.Length > 0) return new PlayUrl(durl, false);
            }
            catch { }
        }
        var outer = "https://music.163.com/song/media/outer/url?id=" + songId + ".mp3";
        try { return await GetRedirectUrlAsync(outer, ct); }
        catch { return new PlayUrl(outer, false); }
    }

    // ─── kugou ───

    public static async Task<List<Song>> KugouSearchAsync(string keyword, int limit, CancellationToken ct)
    {
        var url = "https://songsearch.kugou.com/song_search_v2?keyword=" + Uri.EscapeDataString(keyword) + "&page=1&pagesize=" + limit + "&showtype=1";
        var j = await FetchJsonAsync(url, null, null, new Dictionary<string, string> { ["User-Agent"] = Ua, ["Referer"] = "https://www.kugou.com/" }, ct);
        var list = new List<Song>();
        foreach (var s in (j?["data"]?["lists"] as JsonArray) ?? new JsonArray())
        {
            bool vip = (s?["PayType"]?.GetValue<long?>() ?? 0) != 0 || (s?["Privilege"]?.GetValue<long?>() ?? 0) != 0;
            list.Add(new Song(Safe(s?["FileHash"]), Safe(s?["SongName"]), Safe(s?["SingerName"]), Safe(s?["AlbumName"]), "kugou", vip, Hash: Safe(s?["FileHash"])));
        }
        return list;
    }

    public static async Task<PlayUrl> KugouGetSongUrlAsync(string hash, string? cookie, CancellationToken ct)
    {
        const string mobileUa = "Mozilla/5.0 (Linux; Android 10; SM-G9750) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/83.0.0.0 Mobile Safari/537.36";
        var url = "https://m.kugou.com/app/i/getSongInfo.php?cmd=playInfo&hash=" + hash;
        var headers = new Dictionary<string, string> { ["User-Agent"] = mobileUa, ["Referer"] = "https://m.kugou.com/" };
        if (!string.IsNullOrEmpty(cookie)) headers["Cookie"] = cookie;
        var j = await FetchJsonAsync(url, null, null, headers, ct);
        var playUrl = Safe(j?["url"]);
        return playUrl.Length > 0 ? new PlayUrl(playUrl, false) : new PlayUrl("", true);
    }

    // ─── bilibili ───

    private static string CleanBiliTitle(string title)
    {
        var t = Regex.Replace(title ?? "", "<[^>]+>", "");
        return Regex.Replace(t, "【[^】]*】", "").Trim();
    }

    private static string GenerateBuvid3()
    {
        const string hex = "0123456789ABCDEF";
        var sb = new StringBuilder();
        void Part(int n) { for (var i = 0; i < n; i++) sb.Append(hex[Random.Shared.Next(16)]); }
        Part(8); sb.Append('-'); Part(5); sb.Append('-'); Part(5); sb.Append('-'); Part(5); sb.Append('-'); Part(12);
        return sb.ToString();
    }

    public static async Task<List<Song>> BilibiliSearchAsync(string keyword, int limit, string? cookie, List<string>? upList, CancellationToken ct)
    {
        var url = "https://api.bilibili.com/x/web-interface/search/all/v2?keyword=" + Uri.EscapeDataString(keyword) + "&page=1&page_size=50";
        var c = cookie ?? "";
        if (!c.Contains("buvid3=")) c += (c.Length > 0 ? "; " : "") + "buvid3=" + GenerateBuvid3();
        var headers = new Dictionary<string, string> { ["User-Agent"] = Ua, ["Referer"] = "https://www.bilibili.com", ["Cookie"] = c };
        var j = await FetchJsonAsync(url, null, null, headers, ct);
        if (j is JsonValue) throw new Exception("B站搜索失败: 非 JSON 响应");
        var code = j?["code"]?.GetValue<long?>() ?? -1;
        if (code != 0) throw new Exception("B站搜索失败: " + (Safe(j?["message"]) is var m && m.Length > 0 ? m : "code=" + code));
        var upSet = new HashSet<string>((upList ?? new List<string>()).Select(x => x.ToString()));
        var videos = new List<JsonNode?>();
        foreach (var g in (j?["data"]?["result"] as JsonArray) ?? new JsonArray())
        {
            if (Safe(g?["result_type"]) != "video") continue;
            foreach (var v in (g?["data"] as JsonArray) ?? new JsonArray()) videos.Add(v);
        }
        var filtered = videos
            .Where(v => upSet.Contains(Safe(v?["mid"])))
            .OrderByDescending(v => v?["play"]?.GetValue<long?>() ?? 0)
            .Take(limit > 0 ? limit : 5)
            .Select(v => new Song(Safe(v?["bvid"]), CleanBiliTitle(Safe(v?["title"])), Safe(v?["author"]), "", "bilibili", false,
                Bvid: Safe(v?["bvid"]), Duration: Safe(v?["duration"])))
            .ToList();
        return filtered;
    }

    public static async Task<PlayUrl> BilibiliGetSongUrlAsync(string bvid, string? cookie, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["User-Agent"] = Ua, ["Referer"] = "https://www.bilibili.com" };
        if (!string.IsNullOrEmpty(cookie)) headers["Cookie"] = cookie;
        var view = await FetchJsonAsync("https://api.bilibili.com/x/web-interface/view?bvid=" + bvid, null, null, headers, ct);
        var vcode = view?["code"]?.GetValue<long?>() ?? -1;
        if (vcode != 0) throw new Exception("获取视频信息失败: " + (Safe(view?["message"]) is var m && m.Length > 0 ? m : "code=" + vcode));
        var cid = view?["data"]?["cid"]?.GetValue<long?>();
        var play = await FetchJsonAsync($"https://api.bilibili.com/x/player/playurl?bvid={bvid}&cid={cid}&fnval=16&fnver=0", null, null, headers, ct);
        var pcode = play?["code"]?.GetValue<long?>() ?? -1;
        if (pcode != 0) throw new Exception("获取播放URL失败: " + (Safe(play?["message"]) is var m && m.Length > 0 ? m : "code=" + pcode));
        var audios = ((play?["data"]?["dash"]?["audio"] as JsonArray) ?? new JsonArray()).ToList();
        if (audios.Count == 0) return new PlayUrl("", true);
        var best = audios.OrderByDescending(a => a?["bandwidth"]?.GetValue<long?>() ?? 0).First();
        return new PlayUrl(Safe(best?["baseUrl"]), false);
    }

    // ─── migu ───
    // Recovered from the v5 web player (music.migu.cn/v5/static/js/@migusdk-*.js). Search and
    // lyrics are plain JSON; the listen endpoint obfuscates its response with a 4-byte header
    // (171,205,1,rand) followed by a byte-wise add/subtract of the env-0 key, and requires the
    // signature/birth/channel headers the H5 page sends. No login is needed for the free
    // catalogue — only the 60-second-audition songs (member tracks) refuse a url.
    private const string MiguKey = "Jk8qzuePiJ1qE3mDYhLQ3T73DtDoAhLP";

    private static readonly Dictionary<string, string> MiguHeaders = new()
    {
        ["User-Agent"] = Ua,
        ["Referer"] = "https://music.migu.cn/v5/",
        ["Accept"] = "application/json, */*",
        ["Content-Type"] = "application/json;charset=UTF-8",
        ["signature"] = "1",
        ["birth"] = "h5page",
        ["channel"] = "014X031",
        ["subchannel"] = "014X031",
    };

    /// <summary>Undo the listen-response obfuscation; null when the payload has no valid header.</summary>
    private static string? MiguDecode(byte[] b)
    {
        if (b.Length < 4 || b[0] != 171 || b[1] != 205 || b[2] != 1) return null;
        var r = b[3];
        var k = Encoding.UTF8.GetBytes(MiguKey);
        var plain = new byte[b.Length - 4];
        for (var i = 0; i < plain.Length; i++)
            plain[i] = (byte)((b[4 + i] + r - k[i % k.Length]) & 0xFF);
        return Encoding.UTF8.GetString(plain);
    }

    private static async Task<JsonArray> MiguSearchRawAsync(string keyword, int limit, CancellationToken ct)
    {
        var url = "https://app.c.nf.migu.cn/MIGUM2.0/v1.0/content/search_all.do?text=" + Uri.EscapeDataString(keyword) +
                  "&pageNo=1&pageSize=" + (limit > 0 ? limit : 10) + "&isCopyright=1&sort=1&searchSwitch=%7B%22song%22%3A1%7D";
        var j = await FetchJsonAsync(url, null, null,
            new Dictionary<string, string> { ["User-Agent"] = Ua, ["Referer"] = "https://m.music.migu.cn/" }, ct);
        return (j?["songResultData"]?["result"] as JsonArray) ?? new JsonArray();
    }

    public static async Task<List<Song>> MiguSearchAsync(string keyword, int limit, CancellationToken ct)
    {
        var list = new List<Song>();
        foreach (var s in await MiguSearchRawAsync(keyword, limit, ct))
        {
            var artists = string.Join("/", ((s?["singers"] as JsonArray) ?? new JsonArray()).Select(x => Safe(x?["name"])));
            // chargeAuditions == 1 means "member track, 60s audition only" (listen answers
            // cannotCode 440013 with no url). Measured 2026-09-14 on ~110 songs: chargeAuditions
            // 1 always refused, 0/200 always played; vipType alone is not decisive (vipType 1
            // with chargeAuditions 0 still plays). copyrightId rides in Mid for the play call.
            // The field arrives as a string on some hits and a number on others → ToFlag.
            var vip = ToFlag(s?["chargeAuditions"]) == 1;
            list.Add(new Song(Safe(s?["contentId"]), Safe(s?["name"]), artists, "", "migu", vip,
                Mid: Safe(s?["copyrightId"])));
        }
        return list;
    }

    public static async Task<PlayUrl> MiguGetSongUrlAsync(string contentId, string? copyrightId, CancellationToken ct)
    {
        var url = "https://app.u.nf.migu.cn/strategy/pc/listen/v2.0?contentId=" + Uri.EscapeDataString(contentId) +
                  "&copyrightId=" + Uri.EscapeDataString(copyrightId ?? "") +
                  "&resourceType=2&netType=01&toneFlag=PQ&scene=";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var kv in MiguHeaders) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        using var resp = await Http.SendAsync(req, ct);
        var text = MiguDecode(await resp.Content.ReadAsByteArrayAsync(ct));
        if (text == null) return new PlayUrl("", true);
        var j = JsonNode.Parse(text);
        var playUrl = Safe(j?["data"]?["url"]);
        return playUrl.Length == 0 ? new PlayUrl("", true) : new PlayUrl(playUrl, false);
    }

    /// <summary>Migu lyrics: pick a search hit whose name matches, then download its own LRC.</summary>
    private static async Task<string> MiguLyricsByNameAsync(string song, string artist, CancellationToken ct)
    {
        try
        {
            var items = (await MiguSearchRawAsync(song, 8, ct))
                .Where(x => NameMatches(Safe(x?["name"]), song)).ToList();
            if (items.Count == 0) return "";
            JsonNode? hit = null;
            if (artist.Length > 0)
                hit = items.FirstOrDefault(x => string.Join("/", ((x?["singers"] as JsonArray) ?? new JsonArray())
                    .Select(y => Safe(y?["name"]))).Contains(artist, StringComparison.OrdinalIgnoreCase));
            hit ??= items[0];
            var lyricUrl = Safe(hit?["lyricUrl"]);
            if (lyricUrl.Length == 0) return "";
            using var req = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", Ua);
            req.Headers.TryAddWithoutValidation("Referer", "https://music.migu.cn/");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return "";
            return (await resp.Content.ReadAsStringAsync(ct)).TrimStart('\uFEFF');
        }
        catch { return ""; }
    }

    // ─── unified ───

    public static async Task<List<Song>> SearchAsync(string platform, string keyword, int limit, string? cookie, string? searchType, List<string>? upList, CancellationToken ct)
    {
        List<Song> outList = platform switch
        {
            "qq" => await QqSearchAsync(keyword, limit, searchType ?? "song", ct),
            "netease" => await NeteaseSearchAsync(keyword, limit, searchType ?? "song", ct),
            "migu" => await MiguSearchAsync(keyword, limit, ct),
            "kugou" => await KugouSearchAsync(keyword, limit, ct),
            "bilibili" => await BilibiliSearchAsync(keyword, limit, cookie, upList, ct),
            _ => throw new Exception("不支持的平台: " + platform),
        };
        // Upstream occasionally returns U+FFFD garbled entries (seen on QQ); filter them.
        return outList.Where(x => !(x.Name + x.Artist).Contains('\uFFFD')).ToList();
    }

    public static Task<PlayUrl> GetSongUrlAsync(string platform, Song song, string? cookie, CancellationToken ct) => platform switch
    {
        "qq" => QqGetSongUrlAsync(!string.IsNullOrEmpty(song.Mid) ? song.Mid : song.Id, cookie, ct),
        "netease" => NeteaseGetSongUrlAsync(song.Id, cookie, ct),
        "migu" => MiguGetSongUrlAsync(song.Id, song.Mid, ct),
        "kugou" => KugouGetSongUrlAsync(!string.IsNullOrEmpty(song.Hash) ? song.Hash : song.Id, cookie, ct),
        "bilibili" => BilibiliGetSongUrlAsync(!string.IsNullOrEmpty(song.Bvid) ? song.Bvid : song.Id, cookie, ct),
        _ => Task.FromException<PlayUrl>(new Exception("不支持的平台: " + platform)),
    };

    // 歌词缓存：上游（酷狗/网易云/QQ）时好时坏，OBS 浮层每次重取都可能拿到空结果，于是在
    // 「有词/暂无歌词」之间闪。这里缓存同一首歌的结果 15 分钟；上次是 none 才允许重试。
    private static readonly Dictionary<string, (string Source, string Lrc, DateTime At)> LyricCache = new();
    private static readonly TimeSpan LyricCacheTtl = TimeSpan.FromMinutes(15);

    /// <summary>清空歌词缓存（强制下次重取）。</summary>
    public static void ClearLyricCache() { lock (LyricCache) LyricCache.Clear(); }

    /// <summary>Lyrics lookup: local LRC file → netease / QQ / kugou online. Port of /api/lyrics in server.js.</summary>

    public static async Task<(string Source, string Lrc)> GetLyricsAsync(string song, string artist, string platform, string id, string dataDir, CancellationToken ct)
    {
        var key = string.Join("|", (song ?? "").Trim().ToLowerInvariant(), (artist ?? "").Trim().ToLowerInvariant(), platform, id);
        lock (LyricCache)
        {
            if (LyricCache.TryGetValue(key, out var hit) &&
                (hit.Lrc.Length > 0 || DateTime.UtcNow - hit.At < LyricCacheTtl))
                return (hit.Source, hit.Lrc);
        }
        var res = await GetLyricsUncachedAsync(song, artist, platform, id, dataDir, ct);
        lock (LyricCache)
        {
            if (LyricCache.Count > 200) LyricCache.Clear();   // 简单上限，长跑也不涨内存
            LyricCache[key] = (res.Source, res.Lrc, DateTime.UtcNow);
        }
        return res;
    }

    private static async Task<(string Source, string Lrc)> GetLyricsUncachedAsync(string song, string artist, string platform, string id, string dataDir, CancellationToken ct)
    {
        // Local LRC: name.lrc / artist - name.lrc / name-without-brackets.lrc
        if (song.Length > 0)
        {
            var dir = Path.Combine(dataDir, "lyrics");
            static string Clean(string s) => Regex.Replace(s, "[\\\\/:*?\"<>|]", "_");
            var safeSong = Clean(song);
            var bare = Regex.Replace(song, "[（(【\\[].*?[）)】\\]]", "").Trim();
            var safeBare = Clean(bare);
            var candidates = new List<string> { safeSong + ".lrc" };
            if (artist.Length > 0) candidates.Add(Clean(artist) + " - " + safeSong + ".lrc");
            if (safeBare.Length > 0 && safeBare != safeSong) candidates.Add(safeBare + ".lrc");
            foreach (var name in candidates)
            {
                try
                {
                    var f = Path.Combine(dir, name);
                    if (File.Exists(f)) return ("local", await File.ReadAllTextAsync(f, ct));
                }
                catch { }
            }
        }
        if (platform == "netease" && Regex.IsMatch(id, "^\\d+$"))
        {
            try
            {
                var j = await FetchJsonAsync("https://music.163.com/api/song/lyric?id=" + id + "&lv=1&kv=1&tv=-1", null, null,
                    new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0" }, ct);
                var lrc = Safe(j?["lrc"]?["lyric"]);
                if (lrc.Length > 0) return ("netease", lrc);
            }
            catch { }
        }
        if (platform == "qq" && id.Length > 0)
        {
            try
            {
                var url = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid=" + Uri.EscapeDataString(id) +
                          "&g_tk=5381&loginUin=0&hostUin=0&format=json&inCharset=utf8&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0";
                var j = await FetchJsonAsync(url, null, null,
                    new Dictionary<string, string> { ["Referer"] = "https://y.qq.com/", ["User-Agent"] = "Mozilla/5.0" }, ct);
                // Some responses wrap in jsonp(...): extract the inner JSON via string ops.
                var raw = j is JsonValue v ? v.GetValue<string>() : null;
                if (raw != null)
                {
                    var start = raw.IndexOf('(');
                    var end = raw.LastIndexOf(')');
                    if (start >= 0 && end > start) j = JsonNode.Parse(raw[(start + 1)..end]);
                }
                var b64 = Safe(j?["lyric"]);
                if (b64.Length > 0)
                {
                    var lrc = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    if (lrc.Length > 0) return ("qq", lrc);
                }
            }
            catch { }
        }
        if (platform == "kugou" && song.Length > 0)
        {
            var kugou = await KugouLyricsAsync(song, artist, id, ct);
            if (kugou.Length > 0) return ("kugou", kugou);
        }
        if (platform == "migu" && song.Length > 0)
        {
            var migu = await MiguLyricsByNameAsync(song, artist, ct);
            if (migu.Length > 0) return ("migu", migu);
        }
        // 兜底：按歌名到酷狗匹配。覆盖三种情况——B站视频（没有平台歌词分支）、
        // 条目缺 id（仅网易云/QQ 需要 id）、上面各平台取词失败。
        if (song.Length > 0)
        {
            var kugou = await KugouLyricsAsync(song, artist, "", ct);
            if (kugou.Length > 0) return ("kugou", kugou);
        }
        // 第二层兜底：网易云按歌名搜索取 id 再取词（酷狗对部分关键词返回 0 候选，例如「起风了」）
        if (song.Length > 0)
        {
            var nt = await NeteaseLyricsByNameAsync(song, artist, ct);
            if (nt.Length > 0) return ("netease", nt);
        }
        // 第三层兜底：咪咕曲库自带 LRC，命中范围与酷狗/网易云互补
        if (song.Length > 0)
        {
            var mg = await MiguLyricsByNameAsync(song, artist, ct);
            if (mg.Length > 0) return ("migu", mg);
        }
        return ("none", "");
    }

    /// <summary>粗略判断候选歌名是否与查询匹配（去空白与常见标点后双向包含）。
    /// 上游搜索是模糊的（网易云对无意义关键词也会返回结果），没有这层校验会显示无关歌词。</summary>
    private static bool NameMatches(string candidate, string query)
    {
        static string Norm(string v) =>
            Regex.Replace(v ?? "", "[\\s()（）【】\\[\\]·、,，.。!！?？~～_\\-—]+", "").ToLowerInvariant();
        var a = Norm(candidate);
        var b = Norm(query);
        if (a.Length == 0 || b.Length == 0) return false;
        return a.Contains(b) || b.Contains(a);
    }

    /// <summary>网易云按歌名兜底：搜索取首个（优先歌手匹配）→ 用 id 取歌词。失败返回空串。</summary>
    private static async Task<string> NeteaseLyricsByNameAsync(string song, string artist, CancellationToken ct)
    {
        try
        {
            var headers = new Dictionary<string, string> { ["User-Agent"] = Ua, ["Referer"] = "https://music.163.com" };
            var s1 = await FetchJsonAsync("https://music.163.com/api/search/get?s=" + Uri.EscapeDataString(song) +
                                          "&type=1&offset=0&limit=5", null, null, headers, ct);
            var arr = s1?["result"]?["songs"] as JsonArray;
            if (arr == null || arr.Count == 0) return "";
            var named = arr.Where(x => NameMatches(Safe(x?["name"]), song)).ToList();
            if (named.Count == 0) return "";
            JsonNode? hit = null;
            if (artist.Length > 0)
                hit = named.FirstOrDefault(x => Safe(x?["artists"]?[0]?["name"]).Contains(artist, StringComparison.OrdinalIgnoreCase));
            hit ??= named[0];
            var id = Safe(hit?["id"]);
            if (id.Length == 0) return "";
            var j = await FetchJsonAsync("https://music.163.com/api/song/lyric?id=" + Uri.EscapeDataString(id) + "&lv=1&kv=1&tv=-1",
                null, null, headers, ct);
            return Safe(j?["lrc"]?["lyric"]);
        }
        catch { return ""; }
    }

    /// <summary>酷狗歌词：按歌名（可带 hash）搜索候选 → 下载 LRC。失败返回空串。</summary>
    private static async Task<string> KugouLyricsAsync(string song, string artist, string id, CancellationToken ct)
    {
        try
        {
            var mobileUa = "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 Chrome/83.0.0.0 Mobile Safari/537.36";
            var s1 = await FetchJsonAsync("https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=" + Uri.EscapeDataString(song) +
                                          (id.Length > 0 ? "&hash=" + Uri.EscapeDataString(id) : ""), null, null,
                new Dictionary<string, string> { ["User-Agent"] = mobileUa, ["Referer"] = "https://m.kugou.com/" }, ct);
            var cands = (s1?["candidates"] as JsonArray) ?? new JsonArray();
            // 只接受歌名对得上的候选，避免「起风了」匹配到名字相似但不相干的歌
            var named = cands.Where(x => NameMatches(Safe(x?["song"]).Length > 0 ? Safe(x?["song"]) : Safe(x?["songName"]), song)).ToList();
            JsonNode? hit = null;
            if (artist.Length > 0)
                hit = named.FirstOrDefault(x => (Safe(x?["singer"])).Contains(artist, StringComparison.OrdinalIgnoreCase));
            hit ??= named.FirstOrDefault();
            if (hit == null) return "";
            var s2 = await FetchJsonAsync("https://lyrics.kugou.com/download?ver=1&client=pc&id=" + Uri.EscapeDataString(Safe(hit["id"])) +
                                          "&accesskey=" + Uri.EscapeDataString(Safe(hit["accesskey"])) + "&fmt=lrc&charset=utf8", null, null,
                new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0", ["Referer"] = "https://www.kugou.com/" }, ct);
            var content = Safe(s2?["content"]);
            if (content.Length == 0) return "";
            return Encoding.UTF8.GetString(Convert.FromBase64String(content)).TrimStart('\uFEFF');
        }
        catch { return ""; }
    }
}


// ─────────────────────────────────────────────────────────────────────────────
// Port of lib/song-request.js: danmu "点歌 xxx" parsing, permission checks,
// blacklist, playlist management with persistence.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SongRequestService
{
    private sealed record PlaylistEntry(
        string Id, string Name, string Artist, string Album, string Platform, bool Vip,
        string Requester, long AddedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mid = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hash = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Bvid = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Duration = null);

    private readonly AppConfig _config;
    private readonly object _lock = new();
    private readonly List<PlaylistEntry> _playlist = new();
    private int _currentIndex = -1;

    /// <summary>Set by LivePipeline: (playing index, is playing, playing song id).</summary>
    public Func<(int, bool, string)>? PlayerState { get; set; }

    /// <summary>Set by LivePipeline: start the given playlist index (used by the skip action).</summary>
    public Func<int, Task>? PlayIndex { get; set; }

    /// <summary>Set by LivePipeline: stop playback (skip past the last track).</summary>
    public Action? StopPlaybackAction { get; set; }
    private readonly List<JsonObject> _recent = new();
    private readonly List<(string Name, long Time)> _recentSongs = new();
    private long _lastGlobalRequest;
    private readonly Dictionary<string, long> _userLastRequest = new();
    private readonly Dictionary<string, long> _dailyCount = new();

    public SongRequestService(AppConfig config)
    {
        _config = config;
        LoadPlaylist();
    }

    private static string PlaylistPath => Path.Combine(AppConfig.DataDir, "song-request-playlist.json");

    private JsonObject Config()
        => _config.GetNode("songRequest") as JsonObject ?? new JsonObject();

    private static string S(JsonObject o, string key, string def = "")
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<string>(out var s) ? s ?? def : def;

    private static long N(JsonObject o, string key, long def = 0)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<long>(out var l) ? l : def;

    private static bool B(JsonObject o, string key, bool def = false)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<bool>(out var b) ? b : def;

    private bool Enabled => B(Config(), "enabled");

    private string CookieFor(string platform)
    {
        var cfg = Config();
        if (platform == "bilibili") return _config.Cookie;
        return S((cfg["cookies"] as JsonObject) ?? new JsonObject(), platform);
    }

    /// <summary>Danmu event entry: parse keyword, full pipeline, returns broadcast result (or null).</summary>
    public async Task<object?> OnEventAsync(LiveEvent ev, CancellationToken ct)
    {
        try
        {
            if (!Enabled || ev.Type != "danmu") return null;
            var keyword = S(Config(), "keyword", "点歌");
            if (keyword.Length == 0) keyword = "点歌";
            var m = Regex.Match((ev.Msg ?? "").Trim(), "^" + Regex.Escape(keyword) + "\\s+(.+)$");
            if (!m.Success) return null;
            var songName = m.Groups[1].Value.Trim();
            if (songName.Length == 0) return null;
            return await RequestSongAsync(songName, ev, ct);
        }
        catch (Exception e)
        {
            return new { ok = false, reason = "error", msg = e.Message };
        }
    }

    private async Task<object> RequestSongAsync(string songName, LiveEvent ev, CancellationToken ct)
    {
        var uname = string.IsNullOrEmpty(ev.Uname) ? "未知" : ev.Uname;
        var cfg = Config();

        var perm = CheckPermission(ev, cfg);
        if (!perm.ok) return new { ok = false, reason = perm.reason, songName, uname };

        var bl = CheckBlacklist(cfg, songName, null);
        if (bl != null) return new { ok = false, reason = "blacklist", songName, uname, blacklist = bl };

        var platform = S(cfg, "platform", "qq");
        if (platform.Length == 0) platform = "qq";
        var cookie = CookieFor(platform);

        List<MusicApi.Song> songs;
        try
        {
            var upList = (cfg["bilibiliUpList"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? new List<string>();
            songs = await MusicApi.SearchAsync(platform, songName, 5, cookie, null, upList, ct);
        }
        catch (Exception e)
        {
            return new { ok = false, reason = "search_error", songName, uname, msg = e.Message };
        }
        if (songs.Count == 0) return new { ok = false, reason = "not_found", songName, uname };

        var song = B(cfg, "allowVip") ? songs[0] : (songs.FirstOrDefault(s => !s.Vip) ?? songs[0]);
        var blSong = CheckBlacklist(cfg, song.Name, song.Artist);
        if (blSong != null) return new { ok = false, reason = "blacklist", songName = song.Name, uname, blacklist = blSong };

        AddToPlaylist(song, uname);
        RecordRequest(songName, song.Name, song.Artist, uname, ev.Uid);
        lock (_lock)
        {
            _recentSongs.Insert(0, (songName, DateTimeOffset.Now.ToUnixTimeMilliseconds()));
            if (_recentSongs.Count > 20) _recentSongs.RemoveRange(20, _recentSongs.Count - 20);
        }
        return new { ok = true, song, uname, platform };
    }

    private (bool ok, string reason) CheckPermission(LiveEvent ev, JsonObject cfg)
    {
        var t = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var uid = ev.Uid ?? "";
        if (B(cfg, "guardOnly") && !ev.IsGuard) return (false, "guard_only");
        var fanMedalMin = N(cfg, "fanMedalMin");
        if (fanMedalMin > 0 && ev.MedalLevel < fanMedalMin) return (false, "medal");
        var honorMin = N(cfg, "honorLevelMin");
        if (honorMin > 0 && ev.HonorLevel < honorMin) return (false, "honor");
        var cooldownMs = N(cfg, "cooldownMs", 10000);
        if (cooldownMs > 0 && t - _lastGlobalRequest < cooldownMs) return (false, "cooldown");
        var userCooldownMs = N(cfg, "userCooldownMs", 60000);
        if (userCooldownMs > 0 && _userLastRequest.TryGetValue(uid, out var last) && t - last < userCooldownMs) return (false, "user_cooldown");
        var maxDaily = N(cfg, "maxDaily");
        if (maxDaily > 0)
        {
            var key = DateTime.UtcNow.ToString("yyyy-MM-dd");
            _dailyCount.TryGetValue(key, out var cnt);
            if (cnt >= maxDaily) return (false, "daily_limit");
        }
        var dedupEnabled = B(cfg, "dedupEnabled", true);
        var dedupMs = N(cfg, "dedupMs", 300000);
        if (dedupEnabled && dedupMs > 0)
        {
            lock (_lock)
            {
                foreach (var s in _recentSongs)
                    if (s.Name == (ev.Msg ?? "").Trim() && t - s.Time < dedupMs)
                        return (false, "dedup");
            }
        }
        return (true, "");
    }

    private static JsonNode? CheckBlacklist(JsonObject cfg, string? songName, string? artist)
    {
        foreach (var bl in (cfg["blacklist"] as JsonArray) ?? new JsonArray())
        {
            var type = S(bl as JsonObject ?? new JsonObject(), "type");
            var value = S(bl as JsonObject ?? new JsonObject(), "value");
            if (type == "song" && !string.IsNullOrEmpty(songName) && songName.Contains(value)) return bl?.DeepClone();
            if (type == "artist" && !string.IsNullOrEmpty(artist) && artist.Contains(value)) return bl?.DeepClone();
        }
        return null;
    }

    private void AddToPlaylist(MusicApi.Song song, string requester)
    {
        lock (_lock)
        {
            _playlist.Add(new PlaylistEntry(song.Id, song.Name, song.Artist, song.Album, song.Platform, song.Vip,
                requester ?? "", DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                song.Mid, song.Hash, song.Bvid, song.Duration));
            if (_currentIndex < 0) _currentIndex = 0;
        }
        SavePlaylist();
    }

    public object AddToPlaylistManual(JsonNode? songNode, string requester)
    {
        var mid = SafeStr(songNode?["mid"]);
        var hash = SafeStr(songNode?["hash"]);
        var bvid = SafeStr(songNode?["bvid"]);
        var entry = new PlaylistEntry(
            SafeStr(songNode?["id"]) is var id && id.Length > 0 ? id : (mid.Length > 0 ? mid : (hash.Length > 0 ? hash : bvid)),
            SafeStr(songNode?["name"]), SafeStr(songNode?["artist"]), SafeStr(songNode?["album"]),
            SafeStr(songNode?["platform"]) is var p && p.Length > 0 ? p : "qq",
            B(songNode as JsonObject ?? new JsonObject(), "vip"),
            requester.Length > 0 ? requester : "手动添加",
            DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            mid.Length > 0 ? mid : null, hash.Length > 0 ? hash : null, bvid.Length > 0 ? bvid : null,
            SafeStr(songNode?["duration"]) is var du && du.Length > 0 ? du : null);
        lock (_lock)
        {
            _playlist.Add(entry);
            if (_currentIndex < 0) _currentIndex = 0;
        }
        SavePlaylist();
        return PlaylistPayload();
    }

    private static string SafeStr(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? "" : "";

    /// <summary>移除第 index 项并返回剩余数量（播放器播放结束自动移除时用）。</summary>
    public int RemoveFromPlaylistCount(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _playlist.Count) return _playlist.Count;
            _playlist.RemoveAt(index);
            if (index < _currentIndex) _currentIndex--;
            if (_currentIndex >= _playlist.Count) _currentIndex = _playlist.Count - 1;
            if (_playlist.Count == 0) _currentIndex = -1;
            var remain = _playlist.Count;
            SavePlaylist();
            return remain;
        }
    }

    public object RemoveFromPlaylist(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _playlist.Count) return new { ok = false };
            _playlist.RemoveAt(index);
            if (index < _currentIndex) _currentIndex--;
            if (_currentIndex >= _playlist.Count) _currentIndex = _playlist.Count - 1;
            if (_playlist.Count == 0) _currentIndex = -1;
        }
        SavePlaylist();
        return PlaylistPayload();
    }

    public object ReorderPlaylist(int from, int to)
    {
        lock (_lock)
        {
            if (from < 0 || from >= _playlist.Count || to < 0 || to >= _playlist.Count) return new { ok = false };
            var item = _playlist[from];
            _playlist.RemoveAt(from);
            _playlist.Insert(to, item);
        }
        SavePlaylist();
        return PlaylistPayload();
    }

    public object ClearPlaylist()
    {
        lock (_lock)
        {
            _playlist.Clear();
            _currentIndex = -1;
        }
        SavePlaylist();
        return new { ok = true };
    }

    /// <summary>跳过正在播的那首（对应旧版 next()）。旧实现只把队列游标 +1，既不切歌也不影响
    /// 播放，按钮等于是哑的；现在从播放器索引出发真的切到下一首，越过末首则停止播放。</summary>
    public async Task<object> SkipCurrentAsync()
    {
        var (idx, _, _) = PlayerState?.Invoke() ?? (-1, false, "");
        if (idx < 0 || PlayIndex == null) return new { ok = false, reason = "not_playing" };
        int count;
        lock (_lock) count = _playlist.Count;
        if (idx + 1 >= count)
        {
            StopPlaybackAction?.Invoke();
            return new { ok = true, stopped = true };
        }
        await PlayIndex(idx + 1);
        return new { ok = true, playingIndex = idx + 1 };
    }

    public object PlaylistPayload()
    {
        lock (_lock)
        {
            // The request-queue cursor (_currentIndex) is not the track that is actually
            // playing, so the payload also carries the player's own state — the OBS lyrics
            // overlay follows the audio, and consumers must not have to guess.
            var state = PlayerState?.Invoke() ?? (-1, false, "");
            return new
            {
                playlist = _playlist.Cast<object>().ToList(),
                currentIndex = _currentIndex,
                playingIndex = state.Item1,
                playing = state.Item2,
                playingSongId = state.Item3,
            };
        }
    }

    public async Task<object> GetSongUrlAsync(int index, CancellationToken ct)
    {
        PlaylistEntry? song;
        lock (_lock)
        {
            if (index < 0 || index >= _playlist.Count) return new { ok = false, reason = "invalid_index" };
            song = _playlist[index];
            // Legacy contract (Bin/public/song-player.js): currentIndex IS the playing track —
            // the playlist row badge and the OBS overlay both read it. The port had turned it
            // into a request-queue cursor that nothing consumes, so a row that was not playing
            // (and the overlay) pointed at the wrong song.
            _currentIndex = index;
        }
        SavePlaylist();
        var cookie = CookieFor(song.Platform);
        try
        {
            var result = await MusicApi.GetSongUrlAsync(song.Platform,
                new MusicApi.Song(song.Id, song.Name, song.Artist, song.Album, song.Platform, song.Vip, song.Mid, song.Hash, song.Bvid, song.Duration),
                cookie, ct);
            return new { ok = true, result.Url, result.Vip, song };
        }
        catch (Exception e)
        {
            return new { ok = false, reason = "url_error", msg = e.Message };
        }
    }

    public async Task<List<MusicApi.Song>> SearchAsync(string keyword, string? searchType, CancellationToken ct)
    {
        var cfg = Config();
        var platform = S(cfg, "platform", "qq");
        if (platform.Length == 0) platform = "qq";
        var cookie = CookieFor(platform);
        var st = string.IsNullOrEmpty(searchType) ? "song" : searchType;
        var upList = (cfg["bilibiliUpList"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? new List<string>();

        if (st == "smart" && platform != "bilibili")
        {
            var results = await MusicApi.SearchAsync(platform, keyword, 10, cookie, "song", upList, ct);
            if (results.Count > 0) return results;
            try { results = await MusicApi.SearchAsync(platform, keyword, 10, cookie, "lyric", upList, ct); } catch { }
            if (results.Count > 0) return results;
            try { results = await MusicApi.SearchAsync(platform, keyword, 10, cookie, "artist", upList, ct); } catch { }
            return results;
        }
        return await MusicApi.SearchAsync(platform, keyword, 10, cookie, st, upList, ct);
    }

    public JsonArray Blacklist()
        => (Config()["blacklist"] as JsonArray)?.DeepClone()!.AsArray() ?? new JsonArray();

    public void SaveBlacklist(JsonArray list)
    {
        var cfg = Config();
        cfg["blacklist"] = list.DeepClone();
        PersistSongRequestSection(cfg);
    }

    public void SavePlatformCookie(string platform, string cookie)
    {
        var cfg = Config();
        var cookies = cfg["cookies"] as JsonObject ?? new JsonObject();
        cookies[platform] = cookie;
        cfg["cookies"] = cookies;
        PersistSongRequestSection(cfg);
    }

    public object CookieStatus()
    {
        var cfg = Config();
        var cookies = cfg["cookies"] as JsonObject ?? new JsonObject();
        return new
        {
            // 只认「登录标记」Cookie：登录页一打开就会下发游客 Cookie，
            // 按非空判断会把未登录状态显示成「已配置」（用户实测反馈）。
            qq = MusicLoginService.IsLoggedInCookie("qq", S(cookies, "qq")),
            netease = MusicLoginService.IsLoggedInCookie("netease", S(cookies, "netease")),
            kugou = MusicLoginService.IsLoggedInCookie("kugou", S(cookies, "kugou")),
            bilibili = MusicLoginService.IsLoggedInCookie("bilibili", _config.Cookie),
        };
    }

    public JsonArray UpList()
        => (Config()["bilibiliUpList"] as JsonArray)?.DeepClone()!.AsArray() ?? new JsonArray();

    public JsonArray SetUpList(JsonArray list)
    {
        var cfg = Config();
        cfg["bilibiliUpList"] = list.DeepClone();
        PersistSongRequestSection(cfg);
        return list;
    }

    private void PersistSongRequestSection(JsonObject updated)
    {
        var doc = _config.Snapshot();
        doc["songRequest"] = updated.DeepClone();
        _config.ReplaceFrom(doc);
    }

    private void RecordRequest(string songName, string name, string artist, string uname, string uid)
    {
        var t = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _lastGlobalRequest = t;
        lock (_userLastRequest) _userLastRequest[uid] = t;
        var key = DateTime.UtcNow.ToString("yyyy-MM-dd");
        lock (_dailyCount)
        {
            _dailyCount.TryGetValue(key, out var cnt);
            _dailyCount[key] = cnt + 1;
        }
        lock (_lock)
        {
            _recent.Insert(0, new JsonObject
            {
                ["songName"] = name, ["artist"] = artist, ["uname"] = uname, ["uid"] = uid,
                ["time"] = t, ["status"] = "success",
            });
            if (_recent.Count > 50) _recent.RemoveRange(50, _recent.Count - 50);
        }
    }

    public object Recent()
    {
        lock (_lock)
            return new { recent = _recent.Select(r => r.DeepClone()).Cast<object>().ToList() };
    }

    private void SavePlaylist()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDir);
            object payload;
            lock (_lock) payload = new { playlist = _playlist, currentIndex = _currentIndex };
            // camelCase: the loader below and Bin/server.js both read camelCase, so
            // PascalCase here silently blanked every entry on the next start.
            File.WriteAllText(PlaylistPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }));
        }
        catch { }
    }

    private void LoadPlaylist()
    {
        try
        {
            if (!File.Exists(PlaylistPath)) return;
            var data = JsonNode.Parse(File.ReadAllText(PlaylistPath)) as JsonObject;
            if (data == null) return;
            if (data["playlist"] is JsonArray arr)
            {
                foreach (var node in arr)
                {
                    if (node is not JsonObject o) continue;
                    _playlist.Add(new PlaylistEntry(
                        Sv(o, "id"), Sv(o, "name"), Sv(o, "artist"), Sv(o, "album"), Sv(o, "platform"),
                        Bv(o, "vip"), Sv(o, "requester"), Nv(o, "addedAt"),
                        Sv(o, "mid") is var m && m.Length > 0 ? m : null,
                        Sv(o, "hash") is var h && h.Length > 0 ? h : null,
                        Sv(o, "bvid") is var bv && bv.Length > 0 ? bv : null,
                        Sv(o, "duration") is var du && du.Length > 0 ? du : null));
                }
            }
            _currentIndex = (int)Nv(data, "currentIndex", -1);
        }
        catch { }
    }

    /// <summary>
    /// Case-tolerant reads: the file is camelCase (legacy Bin/server.js format),
    /// but builds before the naming-policy fix wrote PascalCase — tolerate both
    /// instead of loading those entries as blank.
    /// </summary>
    private static bool TryNode(JsonObject o, string key, out JsonNode? node)
    {
        if (o.TryGetPropertyValue(key, out node)) return true;
        var pascal = char.ToUpperInvariant(key[0]) + key.Substring(1);
        return o.TryGetPropertyValue(pascal, out node);
    }

    private static string Sv(JsonObject o, string key, string def = "")
        => TryNode(o, key, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? def : def;

    private static bool Bv(JsonObject o, string key, bool def = false)
        => TryNode(o, key, out var n) && n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;

    private static long Nv(JsonObject o, string key, long def = 0)
        => TryNode(o, key, out var n) && n is JsonValue v && v.TryGetValue<long>(out var l) ? l : def;
}
