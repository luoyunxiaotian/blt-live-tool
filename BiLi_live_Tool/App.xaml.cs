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
                Title = "B站直播助手 (MAUI Demo)",
                Width = 1100,
                Height = 700,
                MinimumWidth = 900,
                MinimumHeight = 600,
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
#endif
    }
}

