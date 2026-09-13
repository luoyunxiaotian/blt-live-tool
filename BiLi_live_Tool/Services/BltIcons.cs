namespace BiLi_live_Tool.Services;

/// <summary>
/// Inline SVG icon library (24×24, stroke-based, inherits currentColor).
///
/// The UI used to rely on emoji for every icon; emoji render differently per
/// Windows font/emoji version and cannot inherit the theme colour. These are
/// hand-drawn simple geometry so they stay crisp at 14–16px and follow the
/// active theme automatically.
/// </summary>
public static class BltIcons
{
    /// <summary>SVG body for an icon name (empty string when unknown).</summary>
    public static string PathOf(string name) => name switch
    {
        // navigation
        "grid" => "<rect x='3' y='3' width='7' height='7' rx='1.5'/><rect x='14' y='3' width='7' height='7' rx='1.5'/><rect x='3' y='14' width='7' height='7' rx='1.5'/><rect x='14' y='14' width='7' height='7' rx='1.5'/>",
        "mic" => "<rect x='9' y='3' width='6' height='11' rx='3'/><path d='M5 11a7 7 0 0 0 14 0'/><path d='M12 18v3'/>",
        "message" => "<path d='M20 15a2 2 0 0 1-2 2H8l-4 3V6a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2z'/>",
        "link" => "<path d='M10 13a5 5 0 0 0 7 0l2-2a5 5 0 0 0-7-7l-1 1'/><path d='M14 11a5 5 0 0 0-7 0l-2 2a5 5 0 0 0 7 7l1-1'/>",
        "music" => "<path d='M9 18V6l10-2v12'/><circle cx='6.5' cy='18' r='2.5'/><circle cx='16.5' cy='16' r='2.5'/>",
        "activity" => "<path d='M3 12h4l3 8 4-16 3 8h4'/>",
        "gift" => "<rect x='3' y='8' width='18' height='12' rx='2'/><path d='M3 12h18M12 8v12'/><path d='M8 8a2.5 2.5 0 0 1 0-5c1.7 0 3 2 4 5 1-3 2.3-5 4-5a2.5 2.5 0 0 1 0 5'/>",
        "file" => "<path d='M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z'/><path d='M14 3v5h5M9 13h6M9 17h4'/>",
        "box" => "<path d='M3 8l9-5 9 5v8l-9 5-9-5z'/><path d='M3 8l9 5 9-5M12 13v8'/>",
        "shield" => "<path d='M12 3l7 3v6c0 5-3.5 8-7 9-3.5-1-7-4-7-9V6z'/>",
        "zap" => "<path d='M13 2 4 14h7l-1 8 9-12h-7z'/>",
        "image" => "<rect x='3' y='4' width='18' height='16' rx='2'/><circle cx='8.5' cy='9.5' r='1.5'/><path d='M4 17l5-5 4 4 3-2 4 4'/>",
        "widget" => "<rect x='8' y='3' width='8' height='4' rx='1'/><path d='M16 5h2a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2h2M8 12h8M8 16h5'/>",
        "gamepad" => "<rect x='2' y='7' width='20' height='10' rx='5'/><path d='M7 12h3M8.5 10.5v3M15.5 11.5h.01M18 13.5h.01'/>",
        "pulse" => "<path d='M2 12h4l2-6 4 12 2-6h8'/>",
        "book" => "<path d='M4 4h7a3 3 0 0 1 3 3v13a2 2 0 0 0-2-2H4z'/><path d='M20 4h-3a3 3 0 0 0-3 3v13a2 2 0 0 1 2-2h4z'/>",
        // controls / states
        "refresh" => "<path d='M20 12a8 8 0 1 1-2.3-5.6'/><path d='M20 4v4h-4'/>",
        "download" => "<path d='M12 3v12M7 11l5 5 5-5M4 20h16'/>",
        "check" => "<circle cx='12' cy='12' r='9'/><path d='M8 12.5l2.5 2.5L16 9.5'/>",
        "lock" => "<rect x='4' y='10' width='16' height='11' rx='2'/><path d='M8 10V7a4 4 0 0 1 8 0v3'/>",
        "copy" => "<rect x='9' y='9' width='12' height='12' rx='2'/><path d='M5 15V5a2 2 0 0 1 2-2h10'/>",
        "external" => "<path d='M14 4h6v6M20 4l-9 9'/><path d='M18 14v4a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4'/>",
        "login" => "<path d='M10 17l5-5-5-5M15 12H3'/><path d='M14 4h5a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-5'/>",
        "logout" => "<path d='M14 7l-5 5 5 5M9 12h12'/><path d='M10 4H5a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h5'/>",
        "trash" => "<path d='M4 7h16M9 7V5h6v2M6 7l1 13h10l1-13'/>",
        "skip" => "<path d='M5 5l9 7-9 7z'/><path d='M19 5v14'/>",
        "sliders" => "<circle cx='12' cy='12' r='3'/><path d='M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M18.4 5.6L17 7M7 17l-1.4 1.4'/>",
        "volume" => "<path d='M4 9h3l4-4v14l-4-4H4z'/><path d='M15 9a4 4 0 0 1 0 6M17.5 6.5a7 7 0 0 1 0 11'/>",
        "palette" => "<circle cx='12' cy='12' r='9'/><circle cx='9' cy='10' r='1.3'/><circle cx='15' cy='10' r='1.3'/><circle cx='9.5' cy='15' r='1.3'/>",
        "folder" => "<path d='M3 7a2 2 0 0 1 2-2h4l2 3h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'/>",
        "thumb" => "<path d='M7 11v9H4v-9zM7 11l4-7a2 2 0 0 1 3 1.8V9h4a2 2 0 0 1 2 2.4l-1.4 7A2 2 0 0 1 16.6 20H7'/>",
        "server" => "<rect x='3' y='4' width='18' height='6' rx='2'/><rect x='3' y='14' width='18' height='6' rx='2'/><path d='M7 7h.01M7 17h.01'/>",
        "bell" => "<path d='M6 9a6 6 0 1 1 12 0c0 5 2 6 2 6H4s2-1 2-6'/><path d='M10 20a2 2 0 0 0 4 0'/>",
        "star" => "<path d='M12 3l2 5 5 2-5 2-2 5-2-5-5-2 5-2z'/>",
        "filter" => "<path d='M4 5h16l-6 7v6l-4-2v-4z'/>",
        "clock" => "<circle cx='12' cy='12' r='9'/><path d='M12 7v5l3 2'/>",
        "chart" => "<path d='M4 20V6M4 20h16M8 20v-7M12 20V9M16 20v-4'/>",
        "search" => "<circle cx='11' cy='11' r='6'/><path d='M20 20l-4.5-4.5'/>",
        _ => "",
    };

    /// <summary>Icon name for an emoji used in the legacy labels (empty when unknown).</summary>
    public static string NameOfEmoji(string emoji) => emoji switch
    {
        "🎛" => "grid",
        "🎙" => "mic",
        "🔊" => "mic",
        "🔈" => "mic",
        "💬" => "message",
        "⚔" => "link",
        "⚡" => "zap",
        "🎵" => "music",
        "🎁" => "gift",
        "📄" => "file",
        "📦" => "box",
        "🛡" => "shield",
        "🖼" => "image",
        "📋" => "widget",
        "📜" => "widget",
        "🎮" => "gamepad",
        "🩺" => "pulse",
        "📘" => "book",
        "🎨" => "palette",
        "🆕" => "download",
        "📁" => "folder",
        "👍" => "thumb",
        "🖥" => "server",
        "🔔" => "bell",
        "🔐" => "lock",
        "🔗" => "link",
        "⭐" => "star",
        "🚫" => "filter",
        "🎭" => "sliders",
        "📢" => "volume",
        "🧩" => "grid",
        "🕒" => "clock",
        "📊" => "chart",
        "🔍" => "search",
        "⏭" => "skip",
        "⬇" => "download",
        "🔄" => "refresh",
        "🖼️" => "image",
        _ => "",
    };
}
