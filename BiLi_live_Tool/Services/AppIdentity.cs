namespace BiLi_live_Tool.Services;

/// <summary>
/// Single source of truth for branding and release coordinates.
///
/// The display name and the GitHub repository used to be spelled out in a dozen
/// places (window title, MAUI title bar, tray tooltip, sidebar brand, updater
/// endpoints, diagnostics probe, authorization card). Renaming the app or moving
/// it to another repository is now a one-file edit — change <see cref="Name"/>
/// and <see cref="RepoName"/> below and everything follows.
///
/// Naming policy: the display name deliberately avoids platform trademarks
/// (「B站」/「哔哩哔哩」) so the released product does not read as an official
/// client; the internal code name (BLT) stays for build labels and user agents.
/// </summary>
public static class AppIdentity
{
    // ── 品牌（改名只需改这两行）───────────────────────────────────────────
    /// <summary>User-facing product name. No platform trademark in it.</summary>
    public const string Name = "直播小帮手";

    /// <summary>
    /// GitHub repository holding the MAUI release line (the Electron line lives
    /// in its own repository). Used by the updater, the diagnostics probe and the
    /// release page links.
    /// </summary>
    public const string RepoName = "blt-live-tool";

    // ── 其余由上面的值派生 ────────────────────────────────────────────────
    public const string RepoOwner = "luoyunxiaotian";

    /// <summary>Latin code name for UA strings / build labels.</summary>
    public const string CodeName = "blt-maui";

    /// <summary>Sidebar / footer build label.</summary>
    public const string BrandTagline = "BLT CONSOLE · MAUI";

    /// <summary>MAUI release tags carry this suffix (see UpdateChecker).</summary>
    public const string ChannelSuffix = "-maui";

    /// <summary>Author's B站 uid — shown by the authorization card.</summary>
    public const string AuthorUid = "10412378";

    public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";
    public static string ReleasesApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases";
    public static string ReleasesPageUrl => $"{RepoUrl}/releases";
    public static string ReleasesLatestPageUrl => $"{ReleasesPageUrl}/latest";

    /// <summary>Author contact line used by the verify-lock card (and clipboard).</summary>
    public static string AuthorContact => $"B站UID {AuthorUid}（{Name}作者）";
}
