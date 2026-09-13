using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace BiLi_live_Tool.Services;

#if !WINDOWS
/// <summary>Stub for non-Windows TFMs.</summary>
public sealed class BiliBrowserWindow
{
    public BiliBrowserWindow(Func<AppConfig?> cfg, Func<Microsoft.Maui.Dispatching.IDispatcher?> dispatcher) { }
    public bool IsVisible => false;
    public string CurrentUrl => "";
    public void Show(nint ownerHwnd, string url) { }
    public void Hide() { }
    public void Reload() { }
    public void GoBack() { }
    public void GoForward() { }
}
#else
using System.Runtime.InteropServices;

/// <summary>
/// Port of src/bili-browser.js (WebContentsView): the bilibili live browser is
/// a separate borderless window OWNED by the main window and docked over its
/// right ~62%; a poller re-docks it when the main window moves/resizes.
/// Bilibili login cookies are captured while visible (the original listened to
/// partition cookie changes → /api/cookie; here written straight to config).
/// </summary>
public sealed class BiliBrowserWindow
{
    private readonly Func<AppConfig?> _config;
    private readonly Func<Microsoft.Maui.Dispatching.IDispatcher?> _dispatcher;

    private Window? _window;
    private Microsoft.UI.Xaml.Window? _platformWindow;
    private Microsoft.Maui.Controls.WebView? _webView;
    private bool _visible;
    private string _lastCookie = "";
    private long _lastRect;
    private CancellationTokenSource? _pollCts;

    public BiliBrowserWindow(Func<AppConfig?> config, Func<Microsoft.Maui.Dispatching.IDispatcher?> dispatcher)
    {
        _config = config;
        _dispatcher = dispatcher;
    }

    public bool IsVisible => _visible;
    public string CurrentUrl
    {
        get
        {
            try { return (_webView?.Source as UrlWebViewSource)?.Url ?? ""; }
            catch { return ""; }
        }
    }

    public void Show(nint ownerHwnd, string url)
    {
        if (ownerHwnd == 0) return;
        RunOnUi(() =>
        {
            try
            {
                EnsureWindow(ownerHwnd);
                if (_window == null || _webView == null) return;
                try
                {
                    _webView.Source = new UrlWebViewSource { Url = url };
                }
                catch { }
                _visible = true;
                StartLoops(ownerHwnd);
                DockToOwner(ownerHwnd);
                try { _platformWindow?.Activate(); } catch { }
            }
            catch { }
        });
    }

    public void Hide()
    {
        RunOnUi(() =>
        {
            _visible = false;
            _pollCts?.Cancel();
            try { _platformWindow?.AppWindow?.Hide(); } catch { }
        });
    }

    public void Reload() => RunOnUi(() => { try { _webView?.Reload(); } catch { } });
    public void GoBack() => RunOnUi(() => { try { if (_webView?.CanGoBack == true) _webView.GoBack(); } catch { } });
    public void GoForward() => RunOnUi(() => { try { if (_webView?.CanGoForward == true) _webView.GoForward(); } catch { } });

    // ----- internals -----

    private void EnsureWindow(nint ownerHwnd)
    {
        if (_window != null) return;
        _webView = new Microsoft.Maui.Controls.WebView
        {
            BackgroundColor = Color.FromRgb(0x14, 0x16, 0x1a),
        };
        var page = new ContentPage
        {
            Content = _webView,
            BackgroundColor = Color.FromRgb(0x14, 0x16, 0x1a),
        };
        Microsoft.Maui.Controls.NavigationPage.SetHasNavigationBar(page, false);
        _window = new Window(page) { Title = "B站直播" };
#if WINDOWS
        var platform = _window.Handler?.PlatformView;
        if (platform is Microsoft.UI.Xaml.Window winUi)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(winUi);
            // Owned by the main window: stays above it, no taskbar entry.
            SetWindowLongPtrW(hwnd, GwlHwndParent, ownerHwnd);
            var style = GetWindowLongPtrW(hwnd, GwlStyle);
            SetWindowLongPtrW(hwnd, GwlStyle, (nint)(style & ~(WsCaption | WsThickframe)));
            var appWindow = winUi.AppWindow;
            if (appWindow != null)
            {
                appWindow.IsShownInSwitchers = false;
                if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                    presenter.SetBorderAndTitleBar(false, false);
            }
            _platformWindow = winUi;
        }
#endif
        Application.Current?.OpenWindow(_window);
        _pollCts = new CancellationTokenSource();
    }

    private void StartLoops(nint ownerHwnd)
    {
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        _ = Task.Run(async () =>
        {
            // Dock tracker: re-dock whenever the owner moves/resizes.
            while (!ct.IsCancellationRequested && _visible)
            {
                RunOnUi(() => DockToOwner(ownerHwnd));
                try { await Task.Delay(150, ct); } catch { break; }
            }
        }, CancellationToken.None);
        _ = Task.Run(async () =>
        {
            // Cookie capture while visible (3s cadence).
            while (!ct.IsCancellationRequested && _visible)
            {
                RunOnUi(() => _ = CaptureCookiesAsync());
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }, CancellationToken.None);
    }

    private void DockToOwner(nint ownerHwnd)
    {
        try
        {
#if WINDOWS
            if (_window == null || !_visible || _platformWindow == null) return;
            if (!GetClientRect(ownerHwnd, out var rc)) return;
            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(ownerHwnd, ref origin)) return;

            var ownerW = rc.Right - rc.Left;
            var ownerH = rc.Bottom - rc.Top;
            var x = origin.X + (int)(ownerW * 0.38);
            var y = origin.Y;
            var w = ownerW - (int)(ownerW * 0.38);
            var h = ownerH;
            var packed = ((long)x << 32) | (uint)(y + w + h);
            if (packed == _lastRect) return;
            _lastRect = packed;
            _platformWindow.AppWindow?.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, w, h));
#endif
        }
        catch { }
    }

    private async Task CaptureCookiesAsync()
    {
        try
        {
#if WINDOWS
            Microsoft.Maui.Controls.WebView? wv;
            wv = _webView;
            if (wv == null || !_visible) return;
            var platformView = wv.Handler?.PlatformView;
            var core = platformView?.GetType().GetProperty("CoreWebView2")?.GetValue(platformView)
                as Microsoft.Web.WebView2.Core.CoreWebView2;
            if (core == null) return;
            var cookies = await core.CookieManager.GetCookiesAsync("https://live.bilibili.com").AsTask();
            string? sessdata = null, jct = null, dede = null;
            foreach (var c in cookies)
            {
                if (c.Name == "SESSDATA") sessdata = c.Value;
                else if (c.Name == "bili_jct") jct = c.Value;
                else if (c.Name == "DedeUserID") dede = c.Value;
            }
            if (string.IsNullOrEmpty(sessdata) && string.IsNullOrEmpty(jct)) return;
            var cookie = $"SESSDATA={sessdata}; bili_jct={jct}; DedeUserID={dede}";
            if (cookie == _lastCookie) return;
            _lastCookie = cookie;
            _config()?.SetBiliCookie(cookie, dede ?? "");
#endif
        }
        catch { }
    }

    private void RunOnUi(Action action)
    {
        var d = _dispatcher();
        if (d != null) d.Dispatch(() => { try { action(); } catch { } });
        else action();
    }

#if WINDOWS
    private const int GwlStyle = -16;
    private const int GwlHwndParent = -8;
    private const long WsCaption = 0x00C00000;
    private const long WsThickframe = 0x00040000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtrW(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);
#endif
}
#endif
