using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace BiLi_live_Tool.Services;

#if !WINDOWS
/// <summary>Stub for non-Windows TFMs.</summary>
public sealed class BiliBrowserWindow : IDisposable
{
    public BiliBrowserWindow(Func<AppConfig?> cfg, Func<Microsoft.Maui.Dispatching.IDispatcher?> dispatcher) { }
    public bool IsVisible => false;
    public string CurrentUrl => "";
    /// <summary>The idle auto-close only exists on Windows (no owned WebView2 window here).</summary>
    public bool AutoClosedForIdle => false;
    public long IdleClosedAt => 0;
    public int IdleClosedMinutes => 0;
    public int IdleCloseMinutes => 10;
    public void Show(nint ownerHwnd, string url) { }
    public void Hide() { }
    public void Reload() { }
    public void GoBack() { }
    public void GoForward() { }
    public void Dispose() { }
}
#else
using System.Runtime.InteropServices;

/// <summary>
/// Port of src/bili-browser.js (WebContentsView): the bilibili live browser is
/// a separate borderless window OWNED by the main window and docked over its
/// right ~62%; a poller re-docks it when the main window moves/resizes.
/// Bilibili login cookies are captured while visible (the original listened to
/// partition cookie changes → /api/cookie; here written straight to config).
/// The owned WebView2 costs ~150MB-0.8GB, so an idle timer tears the window
/// down when nothing happened for biliBrowser.idleCloseMin minutes (0 = off).
/// </summary>
public sealed class BiliBrowserWindow : IDisposable
{
    /// <summary>Idle poll cadence; the WebView2 playing probe runs at the same beat.</summary>
    private const int IdleTickMs = 30_000;
    /// <summary>biliBrowser.idleCloseMin fallback when the key is absent.</summary>
    private const int DefaultIdleCloseMin = 10;
    /// <summary>Upper clamp for the configured threshold (one day).</summary>
    private const int MaxIdleCloseMin = 1440;
    /// <summary>A probe that never comes back is abandoned after this long.</summary>
    private const int ProbeStaleMs = 60_000;

    private readonly Func<AppConfig?> _config;
    private readonly Func<Microsoft.Maui.Dispatching.IDispatcher?> _dispatcher;

    private Window? _window;
    private Microsoft.UI.Xaml.Window? _platformWindow;
    private Microsoft.Maui.Controls.WebView? _webView;
    private bool _visible;
    private string _lastCookie = "";
    private long _lastRect;
    private CancellationTokenSource? _pollCts;

    // ----- idle auto-close state -----
    private System.Threading.Timer? _idleTimer;
    private long _lastActivityMs;        // Environment.TickCount64 of the last activity
    private string _lastActivityUrl = ""; // previous CurrentUrl, for navigation detection
    private long _activityGen;           // bumped per activity; lets a stale probe abandon the close
    private int _idleProbeBusy;          // 1 while a JS playing-probe is in flight
    private long _idleProbeStartMs;      // TickCount64 when that probe started
    private int _autoClosedForIdle;      // 1 when the last teardown came from the idle timer
    private long _idleClosedAtMs;        // Unix ms of the last idle auto-close (0 = never)
    private int _idleClosedMinutes;      // threshold used by that close
    private int _disposed;

    public BiliBrowserWindow(Func<AppConfig?> config, Func<Microsoft.Maui.Dispatching.IDispatcher?> dispatcher)
    {
        _config = config;
        _dispatcher = dispatcher;
        _lastActivityMs = Environment.TickCount64;
        _idleTimer = new System.Threading.Timer(_ => IdleTick(), null, IdleTickMs, IdleTickMs);
    }

    public bool IsVisible => _visible;

    // ----- idle auto-close state exposed to the panel (MainPage → RoomManage) -----

    /// <summary>True when the last teardown was an idle auto-close (not the user's).</summary>
    public bool AutoClosedForIdle => Volatile.Read(ref _autoClosedForIdle) != 0;

    /// <summary>Unix ms of the last idle auto-close (0 = never); a change signals a new UI notice.</summary>
    public long IdleClosedAt => Interlocked.Read(ref _idleClosedAtMs);

    /// <summary>Minutes the last idle auto-close waited (0 = never closed by idle).</summary>
    public int IdleClosedMinutes => Volatile.Read(ref _idleClosedMinutes);

    /// <summary>Effective biliBrowser.idleCloseMin in minutes (0 = the feature is off).</summary>
    public int IdleCloseMinutes => ReadIdleCloseMinutes();
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
        if (ownerHwnd == 0 || Volatile.Read(ref _disposed) != 0) return;
        RunOnUi(() =>
        {
            try
            {
                Volatile.Write(ref _autoClosedForIdle, 0);   // a fresh open supersedes the idle notice
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
                MarkActivity();
            }
            catch { }
        });
    }

    public void Hide()
    {
        RunOnUi(() =>
        {
            _visible = false;
            _lastRect = 0;   // allow the next Show to re-dock at the same rect
            _pollCts?.Cancel();
#if WINDOWS
            // AppWindow.Hide() proved unreliable for owned windows — park the
            // window off-screen instead (Show re-docks it via MoveAndResize).
            try
            {
                if (_platformWindow != null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_platformWindow);
                    MoveWindow(hwnd, -32000, -32000, 400, 300, true);
                }
            }
            catch { }
            try { _platformWindow?.AppWindow?.Hide(); } catch { }
#endif
        });
    }

    /// <summary>
    /// Fully destroys the window + WebView (frees the renderer memory; Hide
    /// only parks it off-screen). Show recreates everything on demand.
    /// </summary>
    public void Close() => RunOnUi(() => CloseInner(false));

    /// <summary>
    /// Stops the idle timer and tears the window down — the app-exit hook
    /// (MainPage window Destroying) calls this so nothing outlives the app.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
        catch { }
        RunOnUi(() => CloseInner(false));
    }

    public void Reload() => RunOnUi(() => { try { _webView?.Reload(); } catch { } MarkActivity(); });
    public void GoBack() => RunOnUi(() => { try { if (_webView?.CanGoBack == true) _webView.GoBack(); } catch { } MarkActivity(); });
    public void GoForward() => RunOnUi(() => { try { if (_webView?.CanGoForward == true) _webView.GoForward(); } catch { } MarkActivity(); });

    // ----- internals -----

    /// <summary>Teardown shared by the user-facing Close and the idle auto-close (UI thread).</summary>
    private void CloseInner(bool byIdle)
    {
        Volatile.Write(ref _autoClosedForIdle, byIdle ? 1 : 0);
        _visible = false;
        _lastRect = 0;
        _pollCts?.Cancel();
        var w = _window;
        _window = null;
        _webView = null;
        _platformWindow = null;
        try
        {
            if (w != null) Application.Current?.CloseWindow(w);
        }
        catch { }
    }

    // ----- idle auto-close -----
    // The owned WebView2 window is the app's biggest memory item (150MB-0.8GB), so
    // an idle timer below the panel tears it down. "Active" = a navigation, a panel
    // action (Show/Reload/GoBack/GoForward), a captured login cookie, or a playing
    // <video> in a visible window. The threshold is read from biliBrowser.idleCloseMin
    // on every tick, so page-side edits apply without a restart.

    /// <summary>Resets the idle clock and invalidates any probe already in flight.</summary>
    private void MarkActivity()
    {
        Interlocked.Exchange(ref _lastActivityMs, Environment.TickCount64);
        Interlocked.Increment(ref _activityGen);
        try { _lastActivityUrl = CurrentUrl; } catch { }
    }

    /// <summary>biliBrowser.idleCloseMin (minutes; 0 = disabled; absent = 10).</summary>
    private int ReadIdleCloseMinutes()
    {
        try
        {
            if (_config()?.GetNode("biliBrowser") is JsonObject node
                && node.TryGetPropertyValue("idleCloseMin", out var v)
                && v is JsonValue jv
                && jv.TryGetValue<double>(out var d))
                return Math.Clamp((int)Math.Round(d), 0, MaxIdleCloseMin);
        }
        catch { }
        return DefaultIdleCloseMin;
    }

    private void IdleTick()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var minutes = ReadIdleCloseMinutes();
            if (minutes <= 0 || Volatile.Read(ref _window) == null)
            {
                // Feature off, or nothing alive to release: keep the clock at "now" so a
                // re-enable / a fresh Show gets the full countdown.
                MarkActivity();
                return;
            }
            // Navigation or redirects count as activity even without a panel click.
            var url = CurrentUrl;
            if (url.Length > 0 && url != _lastActivityUrl)
            {
                _lastActivityUrl = url;
                MarkActivity();
            }
            if (Environment.TickCount64 - Interlocked.Read(ref _lastActivityMs) < minutes * 60_000L) return;

            // Hidden (parked off-screen by Hide): a <video> may keep playing where nobody
            // can see it, so skip the probe and treat the window as idle.
            if (!_visible)
            {
                CloseForIdle(minutes, Interlocked.Read(ref _activityGen));
                return;
            }
            if (!TryBeginProbe()) return;
            _ = ProbeThenCloseAsync(minutes);
        }
        catch { }
    }

    /// <summary>Single-flight guard for the JS probe (a probe that never returns is abandoned).</summary>
    private bool TryBeginProbe()
    {
        if (Interlocked.CompareExchange(ref _idleProbeBusy, 1, 0) != 0)
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _idleProbeStartMs) < ProbeStaleMs) return false;
            Interlocked.Exchange(ref _idleProbeBusy, 0);   // stale guard: retry on the next tick
            return false;
        }
        Interlocked.Exchange(ref _idleProbeStartMs, Environment.TickCount64);
        return true;
    }

    private async Task ProbeThenCloseAsync(int minutes)
    {
        var gen = Interlocked.Read(ref _activityGen);
        try
        {
            var playing = await IsVideoPlayingAsync();
            if (playing == true)
            {
                MarkActivity();   // live video is playing: the user is watching, never close
                return;
            }
            // false = known idle, null = probe unavailable/unknown → both allow the close.
            if (gen != Interlocked.Read(ref _activityGen)) return;   // the panel acted meanwhile
            CloseForIdle(minutes, gen);
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _idleProbeBusy, 0);
        }
    }

    /// <summary>Final re-checks on the UI thread, then the memory-releasing teardown.</summary>
    private void CloseForIdle(int minutes, long gen)
    {
        RunOnUi(() =>
        {
            if (Volatile.Read(ref _disposed) != 0 || _window == null) return;
            if (gen != Interlocked.Read(ref _activityGen)) return;   // the panel acted meanwhile
            if (Environment.TickCount64 - Interlocked.Read(ref _lastActivityMs) < minutes * 60_000L) return;
            Volatile.Write(ref _idleClosedMinutes, minutes);
            Interlocked.Exchange(ref _idleClosedAtMs, DateTimeOffset.Now.ToUnixTimeMilliseconds());
            CloseInner(true);
        });
    }

    /// <summary>
    /// true = a video is playing; false = known idle; null = the probe could not run.
    /// Failures (no WebView2 yet, script blocked, timeout) are "unknown" and never throw.
    /// </summary>
    private async Task<bool?> IsVideoPlayingAsync()
    {
        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            RunOnUi(() => _ = ProbeVideoPlayingAsync(tcs));
            var done = await Task.WhenAny(tcs.Task, Task.Delay(6000)).ConfigureAwait(false);
            return done == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        catch { return null; }
    }

    /// <summary>WebView2 script call; must start on the UI thread (handler-owned object).</summary>
    private async Task ProbeVideoPlayingAsync(TaskCompletionSource<bool?> tcs)
    {
        try
        {
            var wv = _webView;
            if (wv == null) { tcs.TrySetResult(null); return; }
            const string script =
                "(function(){try{var v=document.querySelector('video');"
                + "return (v && !v.paused && !v.ended) ? '1' : '0';}catch(e){return '0';}})()";
            var raw = await wv.EvaluateJavaScriptAsync(script);
            var res = (raw ?? "").Trim().Trim('"');
            if (res == "1" || res.Equals("true", StringComparison.OrdinalIgnoreCase)) tcs.TrySetResult(true);
            else if (res == "0" || res.Equals("false", StringComparison.OrdinalIgnoreCase)) tcs.TrySetResult(false);
            else tcs.TrySetResult(null);
        }
        catch { tcs.TrySetResult(null); }
    }

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
        // NOTE: the Window handler (and thus PlatformView/AppWindow) only exists
        // AFTER OpenWindow — all native styling must run following that call.
        Application.Current?.OpenWindow(_window);
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
            MarkActivity();   // a captured login cookie means the user was just in the browser
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

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(nint hWnd, int x, int y, int w, int h, bool repaint);
#endif
}
#endif
