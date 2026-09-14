using System.Text.Json.Nodes;
using Microsoft.Maui.Controls;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Port of src/music-browser.js: a popup MAUI window with an embedded WebView
/// for music platform logins. While the window is open, cookies for the
/// platform domain are polled and saved into config.songRequest.cookies on
/// change (the Electron version captured via partition cookie events).
/// </summary>
public sealed class MusicLoginService
{
    private static readonly Dictionary<string, (string LoginUrl, string[] CookieUrls)> Platforms = new()
    {
        ["qq"] = ("https://y.qq.com/", new[] { "https://y.qq.com", "https://graph.qq.com" }),
        ["netease"] = ("https://music.163.com/", new[] { "https://music.163.com" }),
        ["kugou"] = ("https://www.kugou.com/", new[] { "https://www.kugou.com", "https://m.kugou.com" }),
    };

    private static readonly object Lock = new();
    private static Window? _window;
    private static Microsoft.Maui.Controls.WebView? _webView;
    private static string _platform = "";
    private static string _lastCookie = "";

    private readonly IServiceProvider _services;

    public MusicLoginService(IServiceProvider services)
    {
        _services = services;
    }

    public void Show(string platform)
    {
        if (string.IsNullOrEmpty(platform) || !Platforms.ContainsKey(platform)) return;
        var page = MainPage.Current;
        if (page == null) return;
        page.Dispatcher.Dispatch(() =>
        {
            lock (Lock)
            {
                if (_window != null)
                {
                    Activate(_window);   // already open → bring to front
                    return;
                }
                _platform = platform;
                _lastCookie = "";
                _webView = new Microsoft.Maui.Controls.WebView
                {
                    Source = new UrlWebViewSource { Url = Platforms[platform].LoginUrl },
                    BackgroundColor = Color.FromRgb(0x14, 0x16, 0x1a),
                };
                var content = new ContentPage { Content = _webView, BackgroundColor = Color.FromRgb(0x14, 0x16, 0x1a) };
                _window = new Window(content)
                {
                    Title = $"音乐平台登录 · {platform}（登录后关闭本窗口即可）",
                    Width = 520,
                    Height = 720,
                };
                _window.Destroying += (_, _) =>
                {
                    _ = CaptureNowAsync();
                    lock (Lock)
                    {
                        _window = null;
                        _webView = null;
                    }
                };
                Activate(_window);
                _ = PollCookiesAsync();
            }
        });
    }

    private static void Activate(Window window)
    {
#if WINDOWS
        // MAUI's Window has no Activate(); the WinUI window does.
        if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUi)
        {
            winUi.Activate();
            return;
        }
#endif
        Application.Current?.OpenWindow(window);
    }

    private async Task PollCookiesAsync()
    {
        while (true)
        {
            Window? win;
            lock (Lock) win = _window;
            if (win == null) break;
            try
            {
                await Task.Delay(2000);
                await CaptureNowAsync();
            }
            catch { break; }
        }
    }

    private async Task CaptureNowAsync()
    {
        try
        {
#if WINDOWS
            string platform;
            Microsoft.Maui.Controls.WebView? wv;
            lock (Lock)
            {
                platform = _platform;
                wv = _webView;
            }
            if (wv == null || !Platforms.TryGetValue(platform, out var info)) return;
            var platformView = wv.Handler?.PlatformView;
            var core = platformView?.GetType().GetProperty("CoreWebView2")?.GetValue(platformView)
                as Microsoft.Web.WebView2.Core.CoreWebView2;
            if (core == null) return;
            var parts = new List<string>();
            foreach (var url in info.CookieUrls)
            {
                foreach (var c in await core.CookieManager.GetCookiesAsync(url).AsTask())
                {
                    if (string.IsNullOrEmpty(c.Value)) continue;
                    parts.Add($"{c.Name}={c.Value}");
                }
            }
            var cookie = string.Join("; ", parts.Distinct());
            if (cookie.Length == 0 || cookie == _lastCookie) return;
            // 登录页一开始就会下发一批游客 Cookie：没有登录标记就不保存，避免误显示「已配置」
            if (!IsLoggedInCookie(platform, cookie)) return;
            _lastCookie = cookie;
            _services.GetService<LivePipeline>()?.SongRequest.SavePlatformCookie(platform, cookie);
#endif
        }
        catch { }
    }

    /// <summary>平台登录标记 Cookie：只有含这些键才算「已登录」，否则会误把游客 Cookie 当登录态。</summary>
    private static readonly Dictionary<string, string[]> LoginMarkers = new()
    {
        ["netease"] = new[] { "MUSIC_U=" },
        ["qq"] = new[] { "qm_keyst=", "qqmusic_key=", "uin=o" },
        ["kugou"] = new[] { "token=" },
        ["bilibili"] = new[] { "SESSDATA=" },
    };

    /// <summary>判定一段 Cookie 是否代表已登录（无标记表时退回「非空」）。</summary>
    public static bool IsLoggedInCookie(string platform, string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return false;
        if (!LoginMarkers.TryGetValue(platform, out var marks)) return true;
        foreach (var m in marks)
        {
            var i = cookie.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var value = cookie[(i + m.Length)..].Split(';')[0].Trim();
            if (value.Length > 0 && value != "0" && value != "o0") return true;
        }
        return false;
    }

    public bool HasSavedCookie(string platform)
    {
        var cookies = _services.GetService<AppConfig>()?.GetNode("songRequest") as JsonObject;
        return cookies?["cookies"] is JsonObject o
            && o.TryGetPropertyValue(platform, out var v)
            && v is System.Text.Json.Nodes.JsonValue val
            && val.TryGetValue<string>(out var s)
            && IsLoggedInCookie(platform, s);
    }

    /// <summary>Clears the saved cookie string; the WebView profile keeps its own cookies (demo note).</summary>
    public void ClearCookies(string platform)
    {
        var services = _services.GetService<AppConfig>();
        if (services == null) return;
        var doc = services.Snapshot();
        if (doc["songRequest"] is JsonObject sr)
        {
            var cookies = sr["cookies"] as JsonObject ?? new JsonObject();
            cookies[platform] = "";
            sr["cookies"] = cookies;
            services.ReplaceFrom(doc);
        }
    }
}
