using System.IO;
using System.Text.Json.Nodes;
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
                Title = AppIdentity.Name,
                Width = 1100,
                Height = 700,
                MinimumWidth = 900,
                MinimumHeight = 600,
            };
#if WINDOWS
            // MAUI draws the window chrome itself on Windows — the system
            // caption APIs (AppWindow/DWM) are ignored, so theme MAUI's own
            // TitleBar. Start from the saved skin so the first paint already
            // matches the panel instead of flashing the console palette.
            try
            {
                var skin = "console";
                try
                {
                    // GetService(Type) on the interface — avoids the null-state
                    // quirk of the generic extension method overload here.
                    var services = MauiProgram.Services;
                    if (services != null
                        && services.GetService(typeof(AppConfig)) is AppConfig cfg
                        && cfg.GetNode("skin") is JsonValue sv
                        && sv.TryGetValue<string>(out var saved)
                        && !string.IsNullOrWhiteSpace(saved))
                        skin = saved;
                }
                catch { }
                var (bgHex, fgHex) = TitleBarColors(skin);
                window.TitleBar = new Microsoft.Maui.Controls.TitleBar
                {
                    Title = AppIdentity.Name,
                    BackgroundColor = Color.FromArgb(bgHex),
                    ForegroundColor = Color.FromArgb(fgHex),
                };
            }
            catch { }
#endif
            window.HandlerChanged += (_, _) =>
            {
                StyleTitleBar(window);
                ApplyWindowIcon(window);
                HookCloseToTray(window);
            };
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

        /// <summary>
        /// Taskbar/Alt-Tab icon from the branded Assets/tray.ico (the MAUI icon is
        /// generated from Resources/AppIcon at build time; this covers window chrome
        /// on unpackaged runs too).
        /// </summary>
        private static void ApplyWindowIcon(Window window)
        {
            try
            {
#if WINDOWS
                if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUi && winUi.AppWindow is { } appWindow)
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "tray.ico");
                    if (File.Exists(path)) appWindow.SetIcon(path);
                }
#endif
            }
            catch { }
        }

        /// <summary>True only for the real exit path (tray menu / API) — otherwise X hides to tray.</summary>
        internal static bool IsReallyQuitting { get; private set; }

        /// <summary>Real quit: lets the close handler pass and shuts the app down.</summary>
        internal static void QuitForReal()
        {
            IsReallyQuitting = true;
            try { Application.Current?.Quit(); } catch { }
        }

        private static bool _closeHooked;

        /// <summary>
        /// Port of the Electron behaviour: the X button minimizes to the tray;
        /// the app keeps running (tray icon left-click restores it).
        /// </summary>
        private static void HookCloseToTray(Window window)
        {
            try
            {
                if (_closeHooked) return;
                if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUi
                    && winUi.AppWindow is { } appWindow)
                {
                    _closeHooked = true;
                    appWindow.Closing += OnAppWindowClosing;
                    ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(winUi), SW_HIDE);   // start hidden is a no-op; harmless
                    // Re-show in case the hide above raced the first paint.
                    ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(winUi), SW_SHOW);
                }
            }
            catch { }
        }

        private static void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (IsReallyQuitting) return;
            args.Cancel = true;   // keep running …
            HideToTray();         // … but get out of the user's way
        }

        /// <summary>Hides the main window (tray left-click brings it back).</summary>
        internal static void HideToTray()
        {
            try
            {
#if WINDOWS
                var win = Current?.Windows?.FirstOrDefault();
                if (win?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window winUi) return;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(winUi);
                ShowWindow(hwnd, SW_HIDE);
#endif
            }
            catch { }
        }

        /// <summary>Tray left-click: hidden → restore; minimized → restore; visible → minimize.</summary>
        internal static void ToggleMainWindow()
        {
            try
            {
#if WINDOWS
                var win = Current?.Windows?.FirstOrDefault();
                var platform = win?.Handler?.PlatformView;
                if (platform == null) return;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(platform);
                if (!IsWindowVisible(hwnd))
                {
                    ShowWindow(hwnd, SW_SHOW);
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
                else if (IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
                else
                {
                    ShowWindow(hwnd, SW_MINIMIZE);
                }
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
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(nint hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(nint hWnd);

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

        /// <summary>
        /// Title bar colors per theme — mirrors each theme's top-strip tokens in
        /// wwwroot/blt.css + themes.css (console default #0B0F14 / #D7DEE8).
        /// </summary>
        internal static (string Bg, string Fg) TitleBarColors(string theme) => theme switch
        {
            "workbench" => ("#F7F8FA", "#1A1D24"),
            "bili" => ("#FFFFFF", "#33333E"),
            "vibrancy" => ("#F5F5F7", "#1D1D1F"),
            "brutal" => ("#F3EEE2", "#141414"),
            "editorial" => ("#F8F5EE", "#22242A"),
            "neon" => ("#0A0614", "#F4EFFF"),
            "hud" => ("#05070B", "#DFE7F2"),
            "classic" => ("#0E0F13", "#EAEAEE"),   // 旧版默认皮肤（石墨黑 + B站粉）
            _ => ("#0B0F14", "#D7DEE8"),
        };

        /// <summary>
        /// Re-themes the window chrome when the UI theme changes (called from
        /// Blazor). MAUI renders the title bar itself on Windows, so the MAUI
        /// <see cref="Microsoft.Maui.Controls.TitleBar"/> is the object that has
        /// to change — AppWindow.TitleBar / DWM accept the colors but never
        /// paint them (that mismatch is why the bar used to stay console-dark).
        /// </summary>
        internal static void ApplyTitleBarForTheme(string theme)
        {
            try
            {
                var (bgHex, fgHex) = TitleBarColors(theme);
                var win = Current?.Windows?.FirstOrDefault();
                if (win == null)
                {
                    TitleBarDebug = $"theme={theme} no-window";
                    return;
                }
                // Window.TitleBar is typed as ITitleBar; the color properties live
                // on the concrete TitleBar we install in CreateWindow.
                if (win.TitleBar is not Microsoft.Maui.Controls.TitleBar tb)
                {
                    tb = new Microsoft.Maui.Controls.TitleBar { Title = AppIdentity.Name };
                    win.TitleBar = tb;
                }
                tb.BackgroundColor = Color.FromArgb(bgHex);
                tb.ForegroundColor = Color.FromArgb(fgHex);

                // Caption buttons (min / max / close) are drawn by the system, not by
                // MAUI's TitleBar, so they keep the OS palette unless the button colors
                // are set explicitly — that is why they used to clash with the skin.
                var btnInfo = "btn=-";
                if (win.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUi
                    && winUi.AppWindow is { } appWindow)
                {
                    var bar = appWindow.TitleBar;
                    var bg = HexToUi(bgHex);
                    var fg = HexToUi(fgHex);
                    var dim = Blend(bg, fg, 0.45);      // inactive glyphs
                    var hover = Blend(bg, fg, 0.14);    // hover plate
                    var pressed = Blend(bg, fg, 0.24);
                    bar.BackgroundColor = bg;
                    bar.InactiveBackgroundColor = bg;
                    bar.ForegroundColor = fg;
                    bar.InactiveForegroundColor = dim;
                    bar.ButtonBackgroundColor = bg;
                    bar.ButtonInactiveBackgroundColor = bg;
                    bar.ButtonForegroundColor = fg;
                    bar.ButtonInactiveForegroundColor = dim;
                    bar.ButtonHoverBackgroundColor = hover;
                    bar.ButtonHoverForegroundColor = fg;
                    bar.ButtonPressedBackgroundColor = pressed;
                    bar.ButtonPressedForegroundColor = fg;
                    btnInfo = $"btn=bg{bgHex}/hover";
                }
                TitleBarDebug = $"theme={theme} bg={bgHex} fg={fgHex} {btnInfo}";
            }
            catch (Exception ex)
            {
                TitleBarDebug = "theme-error: " + ex.Message;
            }
        }

        /// <summary>#RRGGBB → WinUI color.</summary>
        private static Windows.UI.Color HexToUi(string hex)
        {
            var s = hex.TrimStart('#');
            if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
            var r = Convert.ToByte(s.Substring(0, 2), 16);
            var g = Convert.ToByte(s.Substring(2, 2), 16);
            var b = Convert.ToByte(s.Substring(4, 2), 16);
            return Tc(r, g, b);
        }

        /// <summary>Linear mix of two colors (t = weight of the second).</summary>
        private static Windows.UI.Color Blend(Windows.UI.Color a, Windows.UI.Color b, double t)
            => new()
            {
                A = 255,
                R = (byte)Math.Clamp(a.R + (b.R - a.R) * t, 0, 255),
                G = (byte)Math.Clamp(a.G + (b.G - a.G) * t, 0, 255),
                B = (byte)Math.Clamp(a.B + (b.B - a.B) * t, 0, 255),
            };

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

