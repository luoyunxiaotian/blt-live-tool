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
            // In-process audio host: keeps the WASAPI session on this exe so OBS's
            // "Application Audio Capture" can pick it up (see NativeAudio.cs).
            builder.Services.AddSingleton<NativeAudio>();
            builder.Services.AddSingleton<TtsSpeaker>();
            builder.Services.AddSingleton<PanelSoundPlayer>();
            builder.Services.AddSingleton<SongPlayer>();
            builder.Services.AddSingleton<UiBridge>();
            builder.Services.AddScoped<UiKit>();
            builder.Services.AddSingleton<AnnouncementService>();
            builder.Services.AddSingleton(sp => new AppUpdater(sp.GetRequiredService<AppConfig>(), KestrelHost.VersionText));
            // Update probe: shared by the panel card, the top strip badge and the
            // silent start-up check (6h throttle, see UpdateChecker).
            builder.Services.AddSingleton(sp => new UpdateChecker(KestrelHost.VersionText));
            builder.Services.AddSingleton<VerifyService>();
            builder.Services.AddSingleton<DebouncedSaver>();
            builder.Services.AddSingleton<CleanupService>();
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
                    // 清单里的废弃文件列表落到本地，设置页/更新卡就会出现「清理旧文件」按钮
                    try { app.Services.GetRequiredService<CleanupService>().Merge(info.Obsolete, info.Latest); } catch { }
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
                onQuit: App.QuitForReal,   // real exit: X only minimizes to tray
                menu: new TrayService.MenuContext(
                    StatusLine: () =>
                    {
                        try
                        {
                            var hub = app.Services.GetRequiredService<EventHub>();
                            var st = hub.LastStatus;
                            return st.State == "connected" ? $"\u72b6\u6001\uff1a\u5df2\u8fde\u63a5 \u623f\u95f4 {st.RealRoomId}" : "\u72b6\u6001\uff1a\u672a\u8fde\u63a5";
                        }
                        catch { return "\u72b6\u6001\uff1a-"; }
                    },
                    PortLine: () =>
                    {
                        try { return "\u7aef\u53e3\uff1a" + app.Services.GetRequiredService<KestrelHost>().Port; }
                        catch { return "\u7aef\u53e3\uff1a-"; }
                    },
                    OpenPanel: () => App.RestoreMainWindow(),
                    OpenDataDir: () =>
                    {
                        try
                        {
                            var dir = AppConfig.DataDir;   // 安装版在安装根，便携版在 exe 旁
                            Directory.CreateDirectory(dir);
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
                        }
                        catch { }
                    },
                    ServiceStart: () => { try { app.Services.GetRequiredService<KestrelHost>().ServerAction("start"); } catch { } },
                    ServiceStop: () => { try { app.Services.GetRequiredService<KestrelHost>().ServerAction("stop"); } catch { } },
                    ServiceRestart: () => { try { app.Services.GetRequiredService<KestrelHost>().ServerAction("restart"); } catch { } },
                    AutoLaunchGet: () => { try { return AutoLaunchService.Get(); } catch { return false; } },
                    AutoLaunchSet: e => { try { AutoLaunchService.Set(e); } catch { } },

                    // ── 点歌播放控制（托盘右键）──────────────────────────────
                    SongHasTrack: () => { try { return app.Services.GetRequiredService<SongPlayer>().PlayingIndex >= 0; } catch { return false; } },
                    SongIsPlaying: () => { try { return app.Services.GetRequiredService<SongPlayer>().IsPlaying; } catch { return false; } },
                    SongTogglePause: () =>
                    {
                        try
                        {
                            var player = app.Services.GetRequiredService<SongPlayer>();
                            if (player.PlayingIndex < 0) return;      // 没有歌在播时不做事
                            if (player.IsPlaying) player.Pause(); else player.Resume();
                        }
                        catch { }
                    },
                    SongSkip: () => _ = Task.Run(async () =>
                    {
                        // 与页面「跳过当前」同一条路径（真的切下一首，越界则停止）
                        try { await app.Services.GetRequiredService<LivePipeline>().SongRequest.SkipCurrentAsync(); }
                        catch { }
                    }),

                    // ── 两个总开关（写 config 后立刻 ApplyConfig，服务即时生效）──
                    AutoDanmuGet: () => { try { return app.Services.GetRequiredService<LivePipeline>().AutoDanmu.MasterEnabled; } catch { return true; } },
                    AutoDanmuSet: v =>
                    {
                        try
                        {
                            app.Services.GetRequiredService<AppConfig>().SetSectionEnabled("autoDanmu", v);
                            app.Services.GetRequiredService<LivePipeline>().ApplyConfig();
                        }
                        catch { }
                    },
                    TtsGet: () => { try { return app.Services.GetRequiredService<AppConfig>().SectionEnabled("tts", false); } catch { return false; } },
                    TtsSet: v =>
                    {
                        try
                        {
                            app.Services.GetRequiredService<AppConfig>().SetSectionEnabled("tts", v);
                            app.Services.GetRequiredService<LivePipeline>().ApplyConfig();
                        }
                        catch { }
                    }));
#endif

            return app;
        }
    }
}
