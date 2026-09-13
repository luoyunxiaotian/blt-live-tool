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
                bool vip = false;
                if (s?["pay"]?["payplay"] is JsonValue pv && pv.TryGetValue<bool>(out var payplay)) vip = payplay;
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
            list.Add(new Song(
                Safe(s?["id"]), Safe(s?["name"]), artists, Safe(s?["album"]?["name"]), "netease",
                Vip: (s?["fee"]?.GetValue<long?>() ?? 0) != 0));
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

    // ─── unified ───

    public static async Task<List<Song>> SearchAsync(string platform, string keyword, int limit, string? cookie, string? searchType, List<string>? upList, CancellationToken ct)
    {
        List<Song> outList = platform switch
        {
            "qq" => await QqSearchAsync(keyword, limit, searchType ?? "song", ct),
            "netease" => await NeteaseSearchAsync(keyword, limit, searchType ?? "song", ct),
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
        "kugou" => KugouGetSongUrlAsync(!string.IsNullOrEmpty(song.Hash) ? song.Hash : song.Id, cookie, ct),
        "bilibili" => BilibiliGetSongUrlAsync(!string.IsNullOrEmpty(song.Bvid) ? song.Bvid : song.Id, cookie, ct),
        _ => Task.FromException<PlayUrl>(new Exception("不支持的平台: " + platform)),
    };

    /// <summary>Lyrics lookup: local LRC file → netease / QQ / kugou online. Port of /api/lyrics in server.js.</summary>
    public static async Task<(string Source, string Lrc)> GetLyricsAsync(string song, string artist, string platform, string id, string dataDir, CancellationToken ct)
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
            try
            {
                var mobileUa = "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36 Chrome/83.0.0.0 Mobile Safari/537.36";
                var s1 = await FetchJsonAsync("https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=" + Uri.EscapeDataString(song) +
                                              (id.Length > 0 ? "&hash=" + Uri.EscapeDataString(id) : ""), null, null,
                    new Dictionary<string, string> { ["User-Agent"] = mobileUa, ["Referer"] = "https://m.kugou.com/" }, ct);
                var cands = (s1?["candidates"] as JsonArray) ?? new JsonArray();
                JsonNode? hit = null;
                if (artist.Length > 0)
                    hit = cands.FirstOrDefault(x => (Safe(x?["singer"])).Contains(artist, StringComparison.OrdinalIgnoreCase));
                hit ??= cands.FirstOrDefault();
                if (hit != null)
                {
                    var s2 = await FetchJsonAsync("https://lyrics.kugou.com/download?ver=1&client=pc&id=" + Uri.EscapeDataString(Safe(hit["id"])) +
                                                  "&accesskey=" + Uri.EscapeDataString(Safe(hit["accesskey"])) + "&fmt=lrc&charset=utf8", null, null,
                        new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0", ["Referer"] = "https://www.kugou.com/" }, ct);
                    var content = Safe(s2?["content"]);
                    if (content.Length > 0)
                    {
                        var lrc = Encoding.UTF8.GetString(Convert.FromBase64String(content)).TrimStart('\uFEFF');
                        if (lrc.Length > 0) return ("kugou", lrc);
                    }
                }
            }
            catch { }
        }
        return ("none", "");
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

    public object SkipCurrent()
    {
        lock (_lock)
        {
            if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
            {
                _currentIndex++;
                if (_currentIndex >= _playlist.Count) _currentIndex = -1;
            }
            return new { ok = true, currentIndex = _currentIndex };
        }
    }

    public object PlaylistPayload()
    {
        lock (_lock)
            return new { playlist = _playlist.Cast<object>().ToList(), currentIndex = _currentIndex };
    }

    public async Task<object> GetSongUrlAsync(int index, CancellationToken ct)
    {
        PlaylistEntry? song;
        lock (_lock)
        {
            if (index < 0 || index >= _playlist.Count) return new { ok = false, reason = "invalid_index" };
            song = _playlist[index];
        }
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
            qq = !string.IsNullOrEmpty(S(cookies, "qq")),
            netease = !string.IsNullOrEmpty(S(cookies, "netease")),
            kugou = !string.IsNullOrEmpty(S(cookies, "kugou")),
            bilibili = !string.IsNullOrEmpty(_config.Cookie),
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
            File.WriteAllText(PlaylistPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
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
                        S(o, "id"), S(o, "name"), S(o, "artist"), S(o, "album"), S(o, "platform"),
                        B(o, "vip"), S(o, "requester"), N(o, "addedAt"),
                        S(o, "mid") is var m && m.Length > 0 ? m : null,
                        S(o, "hash") is var h && h.Length > 0 ? h : null,
                        S(o, "bvid") is var bv && bv.Length > 0 ? bv : null,
                        S(o, "duration") is var du && du.Length > 0 ? du : null));
                }
            }
            _currentIndex = (int)N(data, "currentIndex", -1);
        }
        catch { }
    }
}
