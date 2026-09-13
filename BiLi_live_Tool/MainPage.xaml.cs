using BiLi_live_Tool.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BiLi_live_Tool
{
    /// <summary>
    /// Hosts the rebuilt native Blazor UI (C 专业控制台 style). The bilibili
    /// live browser remains the owned borderless window (BiliBrowserWindow),
    /// invoked from Blazor pages through MainPage.Current.
    /// </summary>
    public partial class MainPage : ContentPage
    {
        public static MainPage? Current { get; private set; }

        private readonly BiliBrowserWindow _biliBrowser;

        public MainPage()
        {
            InitializeComponent();
            Current = this;
#if WINDOWS
            // Chromium gates HTMLMediaElement.play() behind a user gesture;
            // TTS/panel sounds must play on their own, so allow autoplay.
            blazorWebView.BlazorWebViewInitializing += (_, e) =>
            {
                // EnvironmentOptions can arrive null in some hosts — guard instead of
                // relying on the catch (a thrown NRE still stops the VS debugger).
                var options = e?.EnvironmentOptions;
                if (options == null) return;
                try
                {
                    var args = options.AdditionalBrowserArguments ?? "";
                    if (!args.Contains("autoplay-policy"))
                        options.AdditionalBrowserArguments = args + " --autoplay-policy=no-user-gesture-required";
                }
                catch { }
            };
#endif
            // Startup overlay: painted natively (no WebView2 white flash), themed from
            // the saved skin so the first thing users see already matches their theme.
            ApplyStartupPalette();
            StartStartupWatch();

            _biliBrowser = new BiliBrowserWindow(
                () => MauiProgram.Services?.GetService<AppConfig>(),
                () => Dispatcher);
        }

        protected override void OnHandlerChanged()
        {
            base.OnHandlerChanged();
            // The main window is already registered by the time the page handler exists;
            // OnAppearing retries in case this ran too early.
            HookBrowserTeardown();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            HookBrowserTeardown();
        }

        private bool _teardownHooked;

        /// <summary>
        /// Releases the idle timer and the owned browser window when the main window is
        /// destroyed (App.CreateWindow hooks Window.Destroying as the app's exit path).
        /// </summary>
        private void HookBrowserTeardown()
        {
            if (_teardownHooked) return;
            try
            {
                // Windows[0] is the main window (same assumption as OwnerHwnd below).
                var window = Application.Current?.Windows?.FirstOrDefault();
                if (window == null) return;
                _teardownHooked = true;
                window.Destroying += (_, _) =>
                {
                    try { _biliBrowser.Dispose(); } catch { }
                };
            }
            catch { }
        }

        // ----- bilibili browser window (called from Blazor pages) -----

        private static nint OwnerHwnd()
        {
#if WINDOWS
            try
            {
                var platform = Application.Current?.Windows?.FirstOrDefault()?.Handler?.PlatformView;
                if (platform == null) return 0;
                return WinRT.Interop.WindowNative.GetWindowHandle(platform);
            }
            catch { return 0; }
#else
            return 0;
#endif
        }

        // ----- startup overlay (main line 1: loading animation + visible failure) -----

        private System.Threading.Timer? _startupWatch;
        private int _startupTicks;
        private bool _startupDone;

        /// <summary>Pull brand colours from the saved skin so the splash matches the app.</summary>
        private void ApplyStartupPalette()
        {
            try
            {
                var theme = "console";
                if (MauiProgram.Services?.GetService(typeof(AppConfig)) is AppConfig cfg
                    && cfg.GetNode("skin") is System.Text.Json.Nodes.JsonValue sv
                    && sv.TryGetValue<string>(out var saved) && !string.IsNullOrWhiteSpace(saved))
                    theme = saved;
                var (bgHex, fgHex) = App.TitleBarColors(theme);
                StartupOverlay.BackgroundColor = Color.FromArgb(bgHex);
                StartupBrand.TextColor = Color.FromArgb(fgHex);
                StartupSpinner.Color = Color.FromArgb(fgHex);
                StartupStage.TextColor = Color.FromArgb(fgHex).WithAlpha(0.55f);
            }
            catch { }
        }

        /// <summary>Advance the stage line while services come up; fail loudly after 15s.</summary>
        private void StartStartupWatch()
        {
            _startupTicks = 0;
            _startupWatch = new System.Threading.Timer(_ =>
            {
                if (_startupDone) return;
                _startupTicks++;
                try
                {
                    Dispatcher.Dispatch(() =>
                    {
                        if (_startupDone) return;
                        var host = MauiProgram.Services?.GetService(typeof(KestrelHost)) as KestrelHost;
                        var apiUp = host?.IsRunning == true;
                        if (_startupTicks >= 50 && !apiUp && !StartupRetry.IsVisible)
                        {
                            // 50 × 300ms = 15s without the embedded service → show the reason.
                            ShowStartupError(string.IsNullOrEmpty(host?.LastError)
                                ? "内嵌服务未在 15 秒内就绪"
                                : "内嵌服务启动失败：" + host!.LastError);
                            return;
                        }
                        if (apiUp && _startupTicks >= 84 && !StartupRetry.IsVisible)
                        {
                            // 84 × 300ms = 25s: service is up but the UI never reported
                            // first paint — surface it instead of spinning forever.
                            ShowStartupError("界面渲染未在 25 秒内完成");
                            return;
                        }
                        StartupStage.Text = apiUp ? "正在载入界面…" : "正在启动内嵌服务…";
                    });
                }
                catch { }
            }, null, 300, 300);
        }

        internal void ShowStartupError(string reason)
        {
            try
            {
                StartupError.Text = reason + "　·　可尝试「重试」，或从托盘菜单退出后重新打开。";
                StartupError.IsVisible = true;
                StartupRetry.IsVisible = true;
                StartupSpinner.IsRunning = false;
                StartupSpinner.IsVisible = false;
                StartupStage.Text = "启动失败";
                _startupWatch?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch { }
        }

        private async void OnStartupRetryClicked(object? sender, EventArgs e)
        {
            try
            {
                StartupRetry.IsVisible = false;
                StartupError.IsVisible = false;
                StartupSpinner.IsVisible = true;
                StartupSpinner.IsRunning = true;
                StartupStage.Text = "正在重试…";
                var host = MauiProgram.Services?.GetService(typeof(KestrelHost)) as KestrelHost;
                if (host != null && !host.IsRunning) _ = host.StartAsync();
                _startupDone = false;
                StartStartupWatch();
                await Task.CompletedTask;
            }
            catch { }
        }

        /// <summary>Called through the loopback bridge (/api/maui/app/ui-ready) on first Blazor paint.</summary>
        internal void HideStartupOverlay()
        {
            if (_startupDone) return;
            _startupDone = true;
            try
            {
                _startupWatch?.Change(Timeout.Infinite, Timeout.Infinite);
                Dispatcher.Dispatch(async () =>
                {
                    try
                    {
                        await StartupOverlay.FadeToAsync(0, 180, Easing.CubicOut);
                        StartupOverlay.IsVisible = false;
                    }
                    catch { }
                });
            }
            catch { }
        }

        public void ShowBiliBrowser(string url)
        {
            var owner = OwnerHwnd();
            if (owner == 0) return;
            _biliBrowser.Show(owner, url);
        }

        public void HideBiliBrowser() => _biliBrowser.Hide();
        public void CloseBiliBrowser() => _biliBrowser.Close();

        public bool IsBiliVisible => _biliBrowser.IsVisible;

        public string BiliCurrentUrl => _biliBrowser.CurrentUrl;

        // ----- idle auto-close (config key biliBrowser.idleCloseMin) -----

        /// <summary>True when the idle timer (not the user) tore the browser window down.</summary>
        public bool BiliAutoClosedForIdle => _biliBrowser.AutoClosedForIdle;

        /// <summary>Effective biliBrowser.idleCloseMin in minutes (0 = the feature is off).</summary>
        public int BiliIdleCloseMin => _biliBrowser.IdleCloseMinutes;

        /// <summary>Unix ms of the last idle auto-close (0 = never); a change signals a new notice.</summary>
        public long BiliIdleClosedAt => _biliBrowser.IdleClosedAt;

        /// <summary>Minutes the last idle auto-close waited (0 = never closed by idle).</summary>
        public int BiliIdleClosedMinutes => _biliBrowser.IdleClosedMinutes;

        /// <summary>Panel notice; "" when the last teardown was not an idle auto-close.</summary>
        public string BiliIdleCloseNote()
        {
            if (!_biliBrowser.AutoClosedForIdle) return "";
            var mins = _biliBrowser.IdleClosedMinutes;
            if (mins <= 0) mins = _biliBrowser.IdleCloseMinutes;
            return $"因闲置 {mins} 分钟已自动关闭 B站浏览器";
        }

        public void ReloadBili() => _biliBrowser.Reload();
        public void GoBackBili() => _biliBrowser.GoBack();
        public void GoForwardBili() => _biliBrowser.GoForward();
    }
}
