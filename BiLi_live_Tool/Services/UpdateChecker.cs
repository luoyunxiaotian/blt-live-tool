// UpdateChecker.cs — GitHub Releases update probe (MAUI release channel).
// Ported (rewritten, not copied) from the Electron original Bin/src/updater.js.
//
// Port scope: version check + latest-release metadata + throttling + cached
// state. The Electron original's download pipeline (multi-mirror racing,
// SHA256/size verification, zip extraction, user-file sanitizing, bat-based
// silent installer spawn) is NOT ported yet — that step needs the MAUI release
// packaging decided first (see 开发文档/MAUI移植方案.md 第九章).
//
// Channel rule: MAUI builds publish into the same repository as the Electron
// line, so MAUI tags carry a "-maui" suffix (e.g. v1.2.0-maui). Only those are
// compared — otherwise /releases/latest (the Electron tag) would look like a
// permanent update for every MAUI build.
#if WINDOWS
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace BiLi_live_Tool.Services;

public sealed class UpdateChecker
{
    private const string ReleasesUrl = "https://api.github.com/repos/luoyunxiaotian/bili-live-tool/releases?per_page=30";
    private const string ReleasePageUrl = "https://github.com/luoyunxiaotian/bili-live-tool/releases";
    private const string ChannelSuffix = "-maui";
    private static readonly TimeSpan Throttle = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    // One static client for the process lifetime (avoids socket exhaustion). GitHub
    // rejects requests without a User-Agent.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
        DefaultRequestHeaders =
        {
            UserAgent = { new ProductInfoHeaderValue("bili-live-tool-maui", "1.0") },
            Accept = { new MediaTypeWithQualityHeaderValue("application/vnd.github+json") },
        },
    };

    private readonly string _currentVersion;
    private readonly object _lock = new();
    private UpdateInfo? _last;
    private DateTimeOffset _lastAt;

    public UpdateChecker(string currentVersion)
        => _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion.Trim();

    /// <summary>Cached result of the most recent check (null until the first one).</summary>
    public UpdateInfo? LastResult { get { lock (_lock) return _last; } }

    public DateTimeOffset LastCheckAt { get { lock (_lock) return _lastAt; } }

    /// <summary>
    /// Checks the MAUI release line. <paramref name="force"/> false honours the 6h
    /// throttle and answers from the cached state (no network round trip).
    /// Network failures degrade to a "check failed" result instead of throwing.
    /// </summary>
    public async Task<UpdateInfo> CheckAsync(CancellationToken ct, bool force = false)
    {
        if (!force)
        {
            var cached = Cached();
            if (cached != null) return cached with { Cached = true };
        }
        try
        {
            using var resp = await Http.GetAsync(ReleasesUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var info = PickMauiRelease(doc.RootElement);
            Store(info);
            return info;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = new UpdateInfo(
                false, _currentVersion, "", "", ReleasePageUrl, "", "",
                "", 0, "", "检查失败：" + ex.Message, false, Now());
            Store(failed);
            return failed;
        }
    }

    /// <summary>Cached state while inside the throttle window, else null.</summary>
    private UpdateInfo? Cached()
    {
        lock (_lock)
        {
            if (_last != null && _lastAt != default && DateTimeOffset.UtcNow - _lastAt < Throttle)
                return _last;
        }
        var (info, at) = ReadState();
        if (info != null && at != default && DateTimeOffset.UtcNow - at < Throttle)
        {
            lock (_lock)
            {
                _last = info;
                _lastAt = at;
            }
            return info;
        }
        return null;
    }

    private UpdateInfo PickMauiRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return new UpdateInfo(false, _currentVersion, "", "", ReleasePageUrl, "", "", "", 0, "",
                "更新源返回异常", false, Now());

        JsonElement best = default;
        var bestVer = new[] { 0, 0, 0 };
        var found = false;
        foreach (var rel in root.EnumerateArray())
        {
            var tag = GetString(rel, "tag_name");
            if (tag.Length == 0) continue;
            if (tag.IndexOf(ChannelSuffix, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (rel.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            var ver = ParseVersion(tag);
            if (!found || IsGreater(ver, bestVer))
            {
                best = rel;
                bestVer = ver;
                found = true;
            }
        }
        if (!found)
        {
            return new UpdateInfo(false, _currentVersion, "", "", ReleasePageUrl, "", "", "", 0, "",
                "尚未发布 MAUI 版本（等待首个以 -maui 结尾的 Release）", false, Now());
        }

        var latestTag = GetString(best, "tag_name");
        var hasUpdate = IsGreater(bestVer, ParseVersion(_currentVersion));
        var (assetName, assetSize, assetUrl, assetSha) = PickAsset(best);
        return new UpdateInfo(
            hasUpdate, _currentVersion, latestTag,
            GetString(best, "name"), GetString(best, "html_url"),
            Truncate(GetString(best, "body"), 600), GetString(best, "published_at"),
            assetName, assetSize, assetUrl,
            hasUpdate ? "发现新版本 " + latestTag : "已是最新版本", false, Now())
        { AssetSha256 = assetSha };
    }

    private static (string Name, long Size, string Url, string Sha) PickAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return ("", 0, "", "");
        JsonElement? zip = null;
        JsonElement? any = null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = GetString(a, "name");
            if (name.Length == 0) continue;
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zip ??= a;
            else any ??= a;
        }
        var pick = zip ?? any;
        if (pick == null) return ("", 0, "", "");
        var el = pick.Value;
        long size = 0;
        if (el.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number) size = sz.GetInt64();
        var sha = GetString(el, "digest");   // GitHub may expose "sha256:..." here
        if (sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = sha["sha256:".Length..];
        return (GetString(el, "name"), size, GetString(el, "browser_download_url"), sha);
    }

    // ---- cached state (data/update-state.json) ----

    private static string StatePath => Path.Combine(AppConfig.DataDir, "update-state.json");

    private void Store(UpdateInfo info)
    {
        lock (_lock)
        {
            _last = info;
            _lastAt = DateTimeOffset.UtcNow;
        }
        try
        {
            Directory.CreateDirectory(AppConfig.DataDir);
            var node = new JsonObject
            {
                ["lastCheckAt"] = _lastAt.ToString("o"),
                ["info"] = JsonSerializer.SerializeToNode(info, JsonWeb),
            };
            File.WriteAllText(StatePath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static (UpdateInfo? Info, DateTimeOffset At) ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return (null, default);
            if (JsonNode.Parse(File.ReadAllText(StatePath)) is not JsonObject node) return (null, default);
            var at = DateTimeOffset.TryParse(node["lastCheckAt"]?.GetValue<string>() ?? "", out var parsed)
                ? parsed : default;
            var info = node["info"] is JsonObject io ? io.Deserialize<UpdateInfo>(JsonWeb) : null;
            return (info, at);
        }
        catch { return (null, default); }
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    private static bool IsGreater(int[] a, int[] b)
    {
        for (var i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) return a[i] > b[i];
        }
        return false;
    }

    // "v1.2.3-maui" -> [1, 2, 3]. Strips a leading v/V, then takes the leading
    // digits of the first three dot-separated segments; missing/non-numeric = 0.
    private static int[] ParseVersion(string? version)
    {
        var parts = new int[3];
        if (string.IsNullOrWhiteSpace(version)) return parts;
        var s = version.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        var segments = s.Split('.');
        for (var i = 0; i < segments.Length && i < 3; i++)
        {
            var value = 0;
            foreach (var ch in segments[i])
            {
                if (ch is < '0' or > '9') break;   // stop at "-maui" / "-beta"
                value = (value * 10) + (ch - '0');
            }
            parts[i] = value;
        }
        return parts;
    }

    private static string GetString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(name, out var el)
           && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";
}

/// <summary>Result of a channel check (serialized camelCase to the panel).</summary>
public sealed record UpdateInfo(
    bool HasUpdate,
    string Current,
    string Latest,
    string Name,
    string Url,
    string Notes,
    string PublishedAt,
    string AssetName,
    long AssetSize,
    string AssetUrl,
    string Message,
    bool Cached,
    string CheckedAt)
{
    /// <summary>SHA256 published by the release (may be empty).</summary>
    public string AssetSha256 { get; init; } = "";
}
#else
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Non-Windows stub. Same public surface as the Windows implementation so shared
/// wiring code compiles for every target framework; always reports "no update".
/// </summary>
public sealed class UpdateChecker
{
    private readonly string _currentVersion;

    public UpdateChecker(string currentVersion)
        => _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion.Trim();

    public UpdateInfo? LastResult => null;

    public DateTimeOffset LastCheckAt => default;

    public Task<UpdateInfo> CheckAsync(CancellationToken ct, bool force = false)
        => Task.FromResult(new UpdateInfo(
            false, _currentVersion, "", "", "", "", "", "", 0, "",
            "仅 Windows 版支持应用内更新检查", false,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
}

public sealed record UpdateInfo(
    bool HasUpdate, string Current, string Latest, string Name, string Url,
    string Notes, string PublishedAt, string AssetName, long AssetSize, string AssetUrl,
    string Message, bool Cached, string CheckedAt)
{
    public string AssetSha256 { get; init; } = "";
}
#endif
