using BiLi_live_Tool.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BiLi_live_Tool
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new MainPage())
            {
                Title = "B站直播助手",
                Width = 1100,
                Height = 700,
                MinimumWidth = 900,
                MinimumHeight = 600,
            };
            window.HandlerChanged += (_, _) => StyleTitleBar(window);
            window.Destroying += async (_, _) =>
            {
                MauiProgram.Services.GetService<LiveService>()?.Stop();
                var host = MauiProgram.Services.GetService<KestrelHost>();
                if (host != null)
                {
                    try { await host.StopAsync(); } catch { }
                }
                MauiProgram.Services.GetService<LivePipeline>()?.Dispose();
                MauiProgram.Services.GetService<TtsHost>()?.StopAll();
#if WINDOWS
                TrayService.Shutdown();
#endif
            };
            return window;
        }

        /// <summary>Tray left-click: minimize ↔ restore the main window.</summary>
        internal static void ToggleMainWindow()
        {
            try
            {
#if WINDOWS
                var win = Current?.Windows?.FirstOrDefault();
                var platform = win?.Handler?.PlatformView;
                if (platform == null) return;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(platform);
                if (IsIconic(hwnd))
                    ShowWindow(hwnd, SW_RESTORE);
                else
                    ShowWindow(hwnd, SW_MINIMIZE);
#endif
            }
            catch { }
        }

        /// <summary>Single-instance show request: restore + foreground the main window.</summary>
        internal static void RestoreMainWindow()
        {
            try
            {
#if WINDOWS
                var win = Current?.Windows?.FirstOrDefault();
                var platform = win?.Handler?.PlatformView;
                if (platform == null) return;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(platform);
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
#endif
            }
            catch { }
        }

#if WINDOWS
        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(nint hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hWnd);

        private static Windows.UI.Color Tc(byte r, byte g, byte b)
            => new() { A = 255, R = r, G = g, B = b };

        /// <summary>
        /// Paints the native title bar in the console theme — colors mirror
        /// wwwroot/blt.css tokens (bg-2 #0B0F14, ink #D7DEE8, line #242F3C,
        /// amber #FFB224).
        /// </summary>
        internal static void StyleTitleBar(Window window)
        {
            try
            {
                if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUi
                    && winUi.AppWindow is { } appWindow)
                {
                    var tb = appWindow.TitleBar;
                    var bg = Tc(0x0B, 0x0F, 0x14);
                    tb.BackgroundColor = bg;
                    tb.ForegroundColor = Tc(0xD7, 0xDE, 0xE8);
                    tb.InactiveBackgroundColor = bg;
                    tb.InactiveForegroundColor = Tc(0x5D, 0x6A, 0x7A);
                    // Caption buttons (min/max/close) follow the theme too.
                    tb.ButtonBackgroundColor = bg;
                    tb.ButtonForegroundColor = Tc(0x8B, 0x97, 0xA7);
                    tb.ButtonHoverBackgroundColor = Tc(0x1A, 0x22, 0x2D);
                    tb.ButtonHoverForegroundColor = Tc(0xFF, 0xB2, 0x24);
                    tb.ButtonPressedBackgroundColor = Tc(0x24, 0x2F, 0x3C);
                    tb.ButtonPressedForegroundColor = Tc(0xD7, 0xDE, 0xE8);
                    tb.ButtonInactiveBackgroundColor = bg;
                    tb.ButtonInactiveForegroundColor = Tc(0x5D, 0x6A, 0x7A);
                }
            }
            catch { /* theme paint is cosmetic */ }
        }
#endif
    }
}

