using BiLi_live_Tool.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BiLi_live_Tool
{
    /// <summary>
    /// The migrated panel UI is the whole window (plain WebView navigated to
    /// the legacy UI served by the embedded Kestrel). The bilibili live browser
    /// is a separate owned borderless window (BiliBrowserWindow) docked over
    /// the right ~62% — the same model as the Electron WebContentsView.
    /// </summary>
    public partial class MainPage : ContentPage
    {
        public static MainPage? Current { get; private set; }

        private readonly BiliBrowserWindow _biliBrowser;

        public MainPage()
        {
            InitializeComponent();
            Current = this;
            _biliBrowser = new BiliBrowserWindow(
                () => MauiProgram.Services?.GetService<AppConfig>(),
                () => MainPage.Current?.Dispatcher);
        }

        protected override void OnHandlerChanged()
        {
            base.OnHandlerChanged();
            if (Handler is not null)
            {
                // A WebView without a Source never initializes CoreWebView2 —
                // prime it with placeholder HTML, then navigate when the
                // embedded server answers.
                try
                {
                    panelWebView.Source = new HtmlWebViewSource
                    {
                        Html = "<html><body style='margin:0;background:#14161a'></body></html>",
                    };
                }
                catch { }
                _ = NavigateToPanelAsync();
            }
        }

        // ----- bilibili browser window -----

        public void ShowBiliBrowser(string url)
        {
            var owner = OwnerHwnd();
            if (owner == 0) return;
            _biliBrowser.Show(owner, url);
        }

        public void HideBiliBrowser() => _biliBrowser.Hide();

        public bool IsBiliVisible => _biliBrowser.IsVisible;

        public string BiliCurrentUrl => _biliBrowser.CurrentUrl;

        public string LayoutDebug() => $"biliVisible={_biliBrowser.IsVisible} nav={NavDebug}";

        public void ReloadBili() => _biliBrowser.Reload();
        public void GoBackBili() => _biliBrowser.GoBack();
        public void GoForwardBili() => _biliBrowser.GoForward();

        private static nint OwnerHwnd()
        {
            try
            {
#if WINDOWS
                var platform = Application.Current?.Windows?.FirstOrDefault()?.Handler?.PlatformView;
                if (platform == null) return 0;
                return WinRT.Interop.WindowNative.GetWindowHandle(platform);
#endif
                return 0;
            }
            catch { return 0; }
        }

        // ----- panel navigation -----

        public string NavDebug { get; private set; } = "not-run";

        private async Task NavigateToPanelAsync()
        {
            try
            {
#if WINDOWS
                var platformView = panelWebView.Handler?.PlatformView;
                var coreProp = platformView?.GetType().GetProperty("CoreWebView2");
                if (coreProp == null)
                {
                    NavDebug = "no CoreWebView2 property";
                    return;
                }

                var port = MauiProgram.Services?.GetService<AppConfig>()?.Port ?? 7460;
                var url = $"http://127.0.0.1:{port}/legacy/index.html";

                // CoreWebView2 appears after the control initializes; the embedded
                // Kestrel starts in the background and may need a moment too.
                // Single-instance enforcement guarantees the port eventually
                // answers, so keep probing until it does.
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                while (true)
                {
                    var core = coreProp.GetValue(platformView) as Microsoft.Web.WebView2.Core.CoreWebView2;
                    if (core != null)
                    {
                        try
                        {
                            using var resp = await http.GetAsync(url);
                            if (resp.IsSuccessStatusCode)
                            {
                                core.Navigate(url);
                                NavDebug = "navigated: " + url;
                                return;
                            }
                        }
                        catch (Exception ex) { NavDebug = "probe error: " + ex.Message; }
                    }
                    else NavDebug = "waiting corewebview2";
                    await Task.Delay(300);
                }
#endif
            }
            catch (Exception ex) { NavDebug = "exception: " + ex.Message; }
        }
    }
}
