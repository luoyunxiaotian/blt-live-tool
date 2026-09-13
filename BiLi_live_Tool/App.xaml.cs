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
#if WINDOWS
            // MAUI draws the window chrome itself on Windows — the system
            // caption APIs (AppWindow/DWM) are ignored, so theme MAUI's own
            // TitleBar with the console tokens.
            try
            {
                window.TitleBar = new Microsoft.Maui.Controls.TitleBar
                {
                    Title = "B站直播助手",
                    BackgroundColor = Color.FromArgb("#0B0F14"),
                    ForegroundColor = Color.FromArgb("#D7DEE8"),
                };
            }
            catch { }
#endif
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

        private static Windows.UI.Color Tc(int r, int g, int b)
            => new() { A = 255, R = (byte)r, G = (byte)g, B = (byte)b };

        /// <summary>
        /// Paints the native title bar in the console theme — colors mirror
        /// wwwroot/blt.css tokens (bg-2 #0B0F14, ink #D7DEE8, line #242F3C,
        /// amber #FFB224). The platform window / AppWindow are not always
        /// ready when handlers change, so this retries on a short timer.
        /// </summary>
        internal static string TitleBarDebug { get; private set; } = "not-run";

        internal static void StyleTitleBar(Window window)
        {
            StyleTitleBarOnce(window, 0);
        }

        /// <summary>Re-themes the title bar when the UI theme changes (called from Blazor).</summary>
        internal static void ApplyTitleBarForTheme(string theme)
        {
            try
            {
                var win = Current?.Windows?.FirstOrDefault();
                if (win?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window winUi
                    || winUi.AppWindow is not { } appWindow) return;
                var (bgRgb, fgRgb, btnFgRgb) = theme switch
                {
                    "workbench" => ((0xF7, 0xF8, 0xFA), (0x1A, 0x1D, 0x24), (0x5B, 0x64, 0x72)),
                    "bili" => ((0xFF, 0xFF, 0xFF), (0x33, 0x33, 0x3E), (0x6F, 0x72, 0x80)),
                    "vibrancy" => ((0xF5, 0xF5, 0xF7), (0x1D, 0x1D, 0x1F), (0x56, 0x56, 0x5C)),
                    "brutal" => ((0xF3, 0xEE, 0xE2), (0x14, 0x14, 0x14), (0x4A, 0x4A, 0x4A)),
                    "editorial" => ((0xF8, 0xF5, 0xEE), (0x22, 0x24, 0x2A), (0x5D, 0x58, 0x49)),
                    "neon" => ((0x0A, 0x06, 0x14), (0xF4, 0xEF, 0xFF), (0xE9, 0xE0, 0xFF)),
                    "hud" => ((0x05, 0x07, 0x0B), (0xDF, 0xE7, 0xF2), (0x7C, 0x8B, 0xA1)),
                    _ => ((0x0B, 0x0F, 0x14), (0xD7, 0xDE, 0xE8), (0x8B, 0x97, 0xA7)),
                };
                var tb = appWindow.TitleBar;
                var bg = Tc(bgRgb.Item1, bgRgb.Item2, bgRgb.Item3);
                var fg = Tc(fgRgb.Item1, fgRgb.Item2, fgRgb.Item3);
                var btnFg = Tc(btnFgRgb.Item1, btnFgRgb.Item2, btnFgRgb.Item3);
                tb.BackgroundColor = bg;
                tb.ForegroundColor = fg;
                tb.InactiveBackgroundColor = bg;
                tb.InactiveForegroundColor = btnFg;
                tb.ButtonBackgroundColor = bg;
                tb.ButtonForegroundColor = btnFg;
                tb.ButtonHoverBackgroundColor = Tc(0x1A, 0x22, 0x2D);
                tb.ButtonHoverForegroundColor = Tc(0xFF, 0xB2, 0x24);
                tb.ButtonPressedBackgroundColor = Tc(0x24, 0x2F, 0x3C);
                tb.ButtonPressedForegroundColor = fg;
                tb.ButtonInactiveBackgroundColor = bg;
                tb.ButtonInactiveForegroundColor = btnFg;
                TitleBarDebug += " theme=" + theme;
            }
            catch { }
        }

        private static void StyleTitleBarOnce(Window window, int attempt)
        {
            try
            {
                TitleBarDebug = "attempt=" + attempt;
                if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window winUi)
                {
                    TitleBarDebug += " no-platform-window";
                    RetryTitleBarLater(window, attempt);
                    return;
                }
                if (winUi.AppWindow is not { } appWindow)
                {
                    TitleBarDebug += " no-appwindow";
                    RetryTitleBarLater(window, attempt);
                    return;
                }
                if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
                {
                    TitleBarDebug += " customization-unsupported";
                    return;
                }
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

                // The WinAppSDK TitleBar colors do not always render on unpackaged
                // WinUI apps; drive DWM directly (DWMWA_CAPTION_COLOR etc., Win11+).
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(winUi);
                var caption = unchecked((int)0x00140F0B);   // COLORREF 0x00BBGGRR of #0B0F14
                var textColor = unchecked((int)0x00E8DED7); // #D7DEE8
                var border = unchecked((int)0x003C2F24);    // #242F3C
                var dwm1 = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
                var dwm2 = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColor, sizeof(int));
                var dwm3 = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
                TitleBarDebug += $" dwm={dwm1}/{dwm2}/{dwm3}";
            }
            catch (Exception ex)
            {
                TitleBarDebug += " exception: " + ex.Message;
            }
        }

        private const int DwmwaBorderColor = 34;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static void RetryTitleBarLater(Window window, int attempt)
        {
            if (attempt >= 20) return;
            try
            {
                window.Dispatcher?.DispatchDelayed(TimeSpan.FromMilliseconds(250), () => StyleTitleBarOnce(window, attempt + 1));
            }
            catch { }
        }
#endif
    }
}

