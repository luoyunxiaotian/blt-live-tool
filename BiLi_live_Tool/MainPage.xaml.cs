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
                try
                {
                    var args = e.EnvironmentOptions.AdditionalBrowserArguments ?? "";
                    if (!args.Contains("autoplay-policy"))
                        e.EnvironmentOptions.AdditionalBrowserArguments = args + " --autoplay-policy=no-user-gesture-required";
                }
                catch { }
            };
#endif
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
