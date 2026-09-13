// UpdateChecker.cs — GitHub Releases update probe.
// Ported (rewritten, not copied) from the Electron original Bin/src/updater.js.
//
// Port scope: ONLY the version check against the "latest" GitHub release.
// The Electron original's download pipeline (multi-mirror racing, SHA256/size
// verification, zip extraction, user-file sanitizing, bat-based silent installer
// spawn) is intentionally NOT ported: the MAUI version does not ship an installer
// package yet, so there is nothing to download/apply. When packaging lands, extend
// this class (or add a sibling) with the download/apply logic.

#if WINDOWS
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Probes the GitHub Releases "latest" endpoint and compares the release tag against
/// the running version. All probe failures degrade to a "no update" result object
/// instead of throwing, so callers can treat the return value uniformly.
/// </summary>
public sealed class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/luoyunxiaotian/bili-live-tool/releases/latest";

    // One static client for the process lifetime (avoids socket exhaustion). GitHub
    // rejects requests without a User-Agent; a 10s overall timeout matches the brief.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders =
        {
            UserAgent = { new ProductInfoHeaderValue("bili-live-tool", "1.0") },
            Accept = { new MediaTypeWithQualityHeaderValue("application/vnd.github+json") },
        },
    };

    private readonly string _currentVersion;

    public UpdateChecker(string currentVersion)
    {
        _currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion;
    }

    /// <summary>
    /// Fetches the latest release and returns:
    ///   success -> { hasUpdate, current, latest (tag), name, url, publishedAt }
    ///   failure -> { hasUpdate = false, current, latest = "(检查失败)", error }
    /// </summary>
    public async Task<object> CheckAsync(CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage resp = await Http.GetAsync(ApiUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            string tag = GetString(root, "tag_name");
            string name = GetString(root, "name");
            string url = GetString(root, "html_url");
            string publishedAt = GetString(root, "published_at");

            bool hasUpdate = IsNewer(tag, _currentVersion);
            return new { hasUpdate, current = _currentVersion, latest = tag, name, url, publishedAt };
        }
        catch (Exception ex)
        {
            // Network errors, rate limits (403), JSON issues, cancellation — all fold
            // into the "check failed" result rather than propagating.
            return new { hasUpdate = false, current = _currentVersion, latest = "(检查失败)", error = ex.Message };
        }
    }

    // Segmented Major.Minor.Patch comparison (identical semantics to updater.js).
    private static bool IsNewer(string latestTag, string currentVersion)
    {
        int[] latest = ParseVersion(latestTag);
        int[] current = ParseVersion(currentVersion);
        for (int i = 0; i < 3; i++)
        {
            if (latest[i] != current[i])
                return latest[i] > current[i];
        }
        return false;
    }

    // "v1.2.3-beta.1" -> [1, 2, 3]. Strips a leading v/V, then takes the leading
    // digits of the first three dot-separated segments; missing/non-numeric = 0.
    private static int[] ParseVersion(string? version)
    {
        int[] parts = new int[3];
        if (string.IsNullOrWhiteSpace(version))
            return parts;

        string s = version.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s[1..];

        string[] segments = s.Split('.');
        for (int i = 0; i < segments.Length && i < 3; i++)
        {
            int value = 0;
            foreach (char ch in segments[i])
            {
                if (ch is < '0' or > '9')
                    break; // stop at pre-release suffix, e.g. "3-beta" -> 3
                value = (value * 10) + (ch - '0');
            }
            parts[i] = value;
        }
        return parts;
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement el)
               && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;
    }
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
    public UpdateChecker(string currentVersion)
    {
        // Update probing is Windows-only in this build.
    }

    public Task<object> CheckAsync(CancellationToken ct)
    {
        return Task.FromResult<object>(new
        {
            hasUpdate = false,
            current = string.Empty,
            latest = string.Empty,
            name = string.Empty,
            url = string.Empty,
            publishedAt = string.Empty,
        });
    }
}
#endif
