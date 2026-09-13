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
            app.Services.GetRequiredService<LivePipeline>().Start();
            app.Services.GetRequiredService<TtsHost>().StartEdge();

            // Honor the original autoConnect setting (server.js:1176 parity).
            var config = app.Services.GetRequiredService<AppConfig>();
            if (config.AutoConnect && !string.IsNullOrWhiteSpace(config.RoomId))
                app.Services.GetRequiredService<LiveService>().Start(config.RoomId, config.Cookie);

#if WINDOWS
            // Tray icon must be created on the WinUI UI thread (message pump lives here).
            TrayService.Initialize("B站直播助手 (MAUI Demo)",
                onToggleVisible: App.ToggleMainWindow,
                onQuit: App.QuitForReal);   // real exit: X only minimizes to tray
#endif

            return app;
        }
    }
}
