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

        public void ReloadBili() => _biliBrowser.Reload();
        public void GoBackBili() => _biliBrowser.GoBack();
        public void GoForwardBili() => _biliBrowser.GoForward();
    }
}
