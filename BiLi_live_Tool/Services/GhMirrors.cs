namespace BiLi_live_Tool.Services;

/// <summary>
/// GitHub mirror helpers, ported verbatim from the Electron updater
/// (Bin/src/updater.js, "多镜像静默检查 GitHub Releases latest；节流；下载并自动安装").
/// Repo/asset traffic is frequently throttled or reset on some networks, so every
/// GitHub URL gets a mirror list:
///
///   • JSON/API calls walk the list in order and stop at the first answer — order
///     matters, the official host is always tried first.
///   • Large asset downloads race every mirror in parallel and keep the fastest
///     one; the losers are cancelled. That is what made the in-app update usable
///     in the Electron build.
///
/// Which mirror wins cannot affect integrity: payloads are still checked against
/// the size + SHA256 from the release manifest / asset digest.
/// </summary>
public static class GhMirrors
{
    /// <summary>API mirrors (order matters) — same list and order as updater.js.</summary>
    public static readonly Func<string, string>[] Api =
    {
        static u => u,
        static u => "https://gh.jasonzeng.dev/" + u,
        static u => "https://gitproxy.dev/" + u,
        static u => u.Replace("https://api.github.com", "https://api.github.akams.cn", StringComparison.OrdinalIgnoreCase),
        static u => "https://gh-proxy.com/" + u,
        static u => "https://ghproxy.net/" + u,
        static u => "https://mirror.ghproxy.com/" + u,
    };

    /// <summary>Download mirrors (order matters) — same list and order as updater.js.</summary>
    public static readonly Func<string, string>[] Download =
    {
        static u => u,
        static u => "https://gh.jasonzeng.dev/" + u,
        static u => "https://gitproxy.dev/" + u,
        static u => "https://github.akams.cn/" + u,
        static u => "https://gh-proxy.com/" + u,
        static u => "https://ghproxy.net/" + u,
        static u => "https://mirror.ghproxy.com/" + u,
        static u => "https://ghfast.top/" + u,
    };

    /// <summary>Expands one GitHub URL into its candidate URLs (deduped, order preserved).</summary>
    public static List<string> Expand(Func<string, string>[] maps, string url)
    {
        var list = new List<string>(maps.Length);
        if (string.IsNullOrWhiteSpace(url)) return list;
        foreach (var map in maps)
        {
            string candidate;
            try { candidate = map(url); } catch { continue; }
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!list.Contains(candidate, StringComparer.OrdinalIgnoreCase)) list.Add(candidate);
        }
        return list;
    }

    /// <summary>Human-readable source of a URL, for the user-visible status line.</summary>
    public static string Host(string url)
    {
        try
        {
            var h = new Uri(url).Host;
            return h.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)
                || h.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                ? "GitHub 直连"
                : h;
        }
        catch { return url; }
    }
}
