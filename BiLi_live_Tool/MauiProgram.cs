using BiLi_live_Tool.Services;
using Microsoft.Extensions.Logging;

namespace BiLi_live_Tool
{
    public static class MauiProgram
    {
        public static IServiceProvider Services { get; private set; } = null!;

        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

            // Demo services: config store, event bus, danmu connection, embedded API/WS server.
            builder.Services.AddSingleton<AppConfig>();
            builder.Services.AddSingleton<EventHub>();
            builder.Services.AddSingleton<Recorder>();
            builder.Services.AddSingleton<TtsHost>();
            builder.Services.AddSingleton<LiveService>();
            builder.Services.AddSingleton<LivePipeline>();
            builder.Services.AddSingleton<MusicLoginService>();
            builder.Services.AddSingleton<KeyViewService>();
            builder.Services.AddSingleton<AudioService>();
            builder.Services.AddSingleton<TtsSpeaker>();
            builder.Services.AddSingleton<PanelSoundPlayer>();
            builder.Services.AddSingleton<SongPlayer>();
            builder.Services.AddSingleton<UiBridge>();
            // Update probe: shared by the panel card, the top strip badge and the
            // silent start-up check (6h throttle, see UpdateChecker).
            builder.Services.AddSingleton(sp => new UpdateChecker(KestrelHost.VersionText));
            builder.Services.AddSingleton<VerifyService>();
            builder.Services.AddSingleton<DebouncedSaver>();
            builder.Services.AddSingleton<KestrelHost>();

#if DEBUG
    		builder.Services.AddBlazorWebViewDeveloperTools();
    		builder.Logging.AddDebug();
#endif

            var app = builder.Build();
            Services = app.Services;

#if WINDOWS
            // Single-instance lock (port of app.requestSingleInstanceLock in
            // src/main.js): a second launch signals the first instance to show
            // its window and exits — otherwise it would fail to bind the port
            // and sit on the loading page forever.
            SingleInstance.EnforceOrExit();
#endif

            app.Services.GetRequiredService<KestrelHost>().StartInBackground();

            // Silent update check on start-up (Electron parity: src/main.js R2). Throttled
            // to one probe per 6h and fully detached — failures never affect startup.
            _ = Task.Run(async () =>
            {
                try
                {
                    var checker = app.Services.GetRequiredService<UpdateChecker>();
                    var info = await checker.CheckAsync(CancellationToken.None);
                    if (info.HasUpdate)
                    {
                        System.Diagnostics.Debug.WriteLine($"[update] 发现新版本 {info.Latest}");
                    }
                }
                catch { }
            });
            app.Services.GetRequiredService<LivePipeline>().Start();
            app.Services.GetRequiredService<TtsHost>().StartEdge();

            // White-list authorization (Electron parity: verify-lock). A light poll
            // replaces the original's config.json watcher: any new/changed uid is
            // verified once, and a rejected account locks room connections.
            _ = Task.Run(async () =>
            {
                var verify = app.Services.GetRequiredService<VerifyService>();
                while (true)
                {
                    try { await verify.RefreshAsync(); } catch { }
                    try { await Task.Delay(TimeSpan.FromSeconds(5)); } catch { return; }
                }
            });

            // Honor the original autoConnect setting (server.js:1176 parity).
            var config = app.Services.GetRequiredService<AppConfig>();
            if (config.AutoConnect && !string.IsNullOrWhiteSpace(config.RoomId))
                app.Services.GetRequiredService<LiveService>().Start(config.RoomId, config.Cookie);

#if WINDOWS
            // Tray icon must be created on the WinUI UI thread (message pump lives here).
            TrayService.Initialize(AppIdentity.Name,
                onToggleVisible: App.ToggleMainWindow,
                onQuit: App.QuitForReal);   // real exit: X only minimizes to tray
#endif

            return app;
        }
    }
}
