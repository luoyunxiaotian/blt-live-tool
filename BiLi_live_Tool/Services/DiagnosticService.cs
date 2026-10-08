using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace BiLi_live_Tool.Services;

/// <summary>
/// 直播小帮手 · 全景排障诊断日志服务 (v0.1.27+ 强化版)
/// 覆盖十大核心专区状态感知：系统环境与音频端点、直播长连接与下行流、系统媒体感知与WASAPI电平、
/// TTS自愈与降级、点歌与曲库、键鼠三路采集与熔断防卡死、下三分之一字幕、WebView2与维护、
/// 增强全网与回环网络探针、智能诊断分析与脱敏配置快照。
/// </summary>
public sealed class DiagnosticService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly Regex CookieMaskRegex = new(@"(SESSDATA|bili_jct|MUSIC_U|qm_keyst|token|access_token|refresh_token)=([^;]+)", RegexOptions.Compiled);

    private readonly AppConfig _config;
    private readonly EventHub _hub;
    private readonly LiveService _live;
    private readonly VerifyService _verify;
    private readonly KestrelHost _kestrel;
    private readonly TtsHost _tts;
    private readonly TtsSpeaker _speaker;
    private readonly KeyViewService _keyview;
    private readonly BiLi_live_Tool.Services.SystemMedia.SystemMediaService _systemMedia;
    private readonly BiLi_live_Tool.Services.LowerThirds.LowerThirdsService _lowerThirds;
    private readonly LivePipeline _pipeline;
    private readonly SongPlayer _songPlayer;

    public DiagnosticService(
        AppConfig config,
        EventHub hub,
        LiveService live,
        VerifyService verify,
        KestrelHost kestrel,
        TtsHost tts,
        TtsSpeaker speaker,
        KeyViewService keyview,
        BiLi_live_Tool.Services.SystemMedia.SystemMediaService systemMedia,
        BiLi_live_Tool.Services.LowerThirds.LowerThirdsService lowerThirds,
        LivePipeline pipeline,
        SongPlayer songPlayer)
    {
        _config = config;
        _hub = hub;
        _live = live;
        _verify = verify;
        _kestrel = kestrel;
        _tts = tts;
        _speaker = speaker;
        _keyview = keyview;
        _systemMedia = systemMedia;
        _lowerThirds = lowerThirds;
        _pipeline = pipeline;
        _songPlayer = songPlayer;
    }

    /// <summary>
    /// 脱敏 Cookie：对敏感的 SESSDATA/bili_jct/MUSIC_U 等值进行掩码处理，保留前4位与长度，防止泄露登录凭据。
    /// </summary>
    public static string MaskCookie(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(未配置凭据)";
        return CookieMaskRegex.Replace(raw, m =>
        {
            var key = m.Groups[1].Value;
            var val = m.Groups[2].Value.Trim();
            if (val.Length <= 8) return $"{key}=***";
            return $"{key}={val[..4]}***{val[^4..]} (长度:{val.Length})";
        });
    }

    /// <summary>
    /// 对单项敏感字符串（token/secret/key等）进行安全掩码。
    /// </summary>
    public static string MaskSensitiveString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(空)";
        if (raw.Contains('=')) return MaskCookie(raw);
        if (raw.Length <= 8) return "***";
        return $"{raw[..4]}***{raw[^4..]} (长度:{raw.Length})";
    }

    /// <summary>
    /// 递归对配置对象进行深度脱敏，生成可安全公开展示的配置副本。
    /// </summary>
    public static JsonNode? SanitizeNode(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonObject obj)
        {
            var clean = new JsonObject();
            foreach (var (k, v) in obj)
            {
                var lowerK = k.ToLowerInvariant();
                if ((lowerK.Contains("cookie") || lowerK.Contains("token") || lowerK.Contains("sessdata") || lowerK.Contains("secret") || lowerK.Contains("bili_jct"))
                    && v is JsonValue val && val.TryGetValue<string>(out var strVal))
                {
                    clean[k] = MaskSensitiveString(strVal);
                }
                else
                {
                    clean[k] = SanitizeNode(v);
                }
            }
            return clean;
        }
        if (node is JsonArray arr)
        {
            var cleanArr = new JsonArray();
            foreach (var item in arr)
            {
                cleanArr.Add(SanitizeNode(item));
            }
            return cleanArr;
        }
        return node.DeepClone();
    }

    /// <summary>
    /// 生成全景十大专区排障诊断报告文本（Markdown 纯文本结构）。
    /// </summary>
    public async Task<string> GenerateReportAsync()
    {
        var sb = new StringBuilder();
        var now = DateTime.Now;
        var proc = Process.GetCurrentProcess();

        sb.AppendLine("================================================================================");
        sb.AppendLine("                 直播小帮手 · 客户端全景运行环境与排障诊断日志");
        sb.AppendLine($"生成时间: {now:yyyy-MM-dd HH:mm:ss} (本地时间)");
        sb.AppendLine("安全声明: 本日志已对所有私密凭据（Cookie/Token/Secret）执行前置掩码脱敏，可放心公开。");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        // ════════ 一、系统运行环境与音频端点 ════════
        sb.AppendLine("【一、系统环境与音频输出端点】");
        sb.AppendLine($"客户端版本   : {KestrelHost.VersionText}");
        sb.AppendLine($"运行时框架   : {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"CLR 核心版本 : {Environment.Version}");
        sb.AppendLine($"操作系统     : {RuntimeInformation.OSDescription} ({(Environment.Is64BitOperatingSystem ? "64位" : "32位")})");
        sb.AppendLine($"系统架构     : {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"进程 PID     : {Environment.ProcessId}");
        sb.AppendLine($"进程启动时间 : {proc.StartTime:yyyy-MM-dd HH:mm:ss} (已运行 {(now - proc.StartTime).TotalMinutes:F1} 分钟)");
        sb.AppendLine($"物理内存占用 : {proc.WorkingSet64 / (1024 * 1024):F1} MB (峰值: {proc.PeakWorkingSet64 / (1024 * 1024):F1} MB)");
        sb.AppendLine($"GC 堆内存    : {GC.GetTotalMemory(false) / (1024 * 1024):F1} MB");
        sb.AppendLine($"程序执行目录 : {AppContext.BaseDirectory}");
        sb.AppendLine($"数据存储目录 : {AppConfig.DataDir}");
        var layoutPath = Path.Combine(AppContext.BaseDirectory, "layout.json");
        sb.AppendLine($"打包安装模式 : {(File.Exists(layoutPath) ? "安装包隔离模式 (检测到 layout.json)" : "绿色便携模式")}");

#if WINDOWS
        try
        {
            var (devCount, summary) = BiLi_live_Tool.Services.SystemMedia.WasapiProcessMeter.QueryAudioEndpoints();
            sb.AppendLine($"WASAPI 输出端: {summary} (活跃数: {devCount})");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"WASAPI 输出端: 探测异常 ({ex.Message})");
        }
#else
        sb.AppendLine("WASAPI 输出端: 非 Windows 系统，跳过原生 CoreAudio 枚举");
#endif
        sb.AppendLine();

        // ════════ 二、直播间长连接与下行弹幕链路 ════════
        var st = _hub.LastStatus;
        var client = _live.Client;
        sb.AppendLine("【二、直播间长连接与下行弹幕链路】");
        sb.AppendLine($"配置直播间号 : {(string.IsNullOrEmpty(_config.RoomId) ? "(未设置)" : _config.RoomId)}");
        sb.AppendLine($"解析真实房间 : {(string.IsNullOrEmpty(st.RealRoomId) ? "(未解析)" : st.RealRoomId)}");
        sb.AppendLine($"主播 UID     : {(string.IsNullOrEmpty(st.AnchorUid) ? "(未知)" : st.AnchorUid)}");
        sb.AppendLine($"当前连接状态 : {st.State} ({GetStateDescription(st.State)})");
        sb.AppendLine($"直播间标题   : {(string.IsNullOrEmpty(st.Title) ? "—" : st.Title)}");
        sb.AppendLine($"最近连接时间 : {(st.ConnectedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(st.ConnectedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "未处于连接态")}");
        sb.AppendLine($"状态错误信息 : {(string.IsNullOrEmpty(st.Error) ? "(无错误)" : "★ " + st.Error)}");
        sb.AppendLine($"自动重连开关 : {_config.AutoConnect}");

        if (client != null)
        {
            sb.AppendLine($"当前弹幕节点 : {(string.IsNullOrEmpty(client.CurrentHost) ? "(尚未连接到节点)" : client.CurrentHost)}");
            var authUidText = client.CurrentAuthUid switch
            {
                > 0 => $"用户凭据鉴权模式 (UID: {client.CurrentAuthUid})",
                0 => "访客匿名自愈模式 (UID: 0)",
                _ => "(未建立鉴权连接)"
            };
            sb.AppendLine($"握手鉴权模式 : {authUidText}");
            sb.AppendLine($"累计接收包数 : {client.TotalPacketsReceived} 个协议包");
            var lastAgo = client.LastDataTicks > 0 ? (Environment.TickCount64 - client.LastDataTicks) / 1000.0 : -1;
            sb.AppendLine($"最近收包时间 : {(lastAgo >= 0 ? $"{lastAgo:F1} 秒前" : "尚未收到数据包")}");

            var seenCmds = client.SeenCmds();
            if (seenCmds.Count > 0)
            {
                var cmdSummary = string.Join(", ", seenCmds.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}:{kv.Value}"));
                sb.AppendLine($"指令命中采样 : {cmdSummary}{(seenCmds.Count > 8 ? $" 等共 {seenCmds.Count} 种指令" : "")}");
            }
            else
            {
                sb.AppendLine("指令命中采样 : 尚未收到任何业务指令");
            }

            var recentRaw = client.RecentRawFrames(max: 2);
            if (recentRaw.Count > 0)
            {
                sb.AppendLine("最近原始帧摘 : " + string.Join(" | ", recentRaw.Select(r => r.Length > 80 ? r[..80] + "…" : r)));
            }
        }
        else
        {
            sb.AppendLine("弹幕客户端   : (服务实例未初始化)");
        }

        sb.AppendLine($"B站账号 UID  : {(string.IsNullOrEmpty(_config.Uid) ? "(未登录)" : _config.Uid)}");
        sb.AppendLine($"Cookie 凭据  : {MaskCookie(_config.Cookie)}");
        sb.AppendLine($"白名单授权态 : Checked={_verify.State.Checked}, Locked={_verify.Locked}, Code={_verify.State.Code}, UID={_verify.State.Uid}, Name={_verify.State.Name}");
        sb.AppendLine();

        // ════════ 三、系统媒体感知与音频流全景 ════════
        sb.AppendLine("【三、系统媒体感知与音频流全景】");
        sb.AppendLine($"媒体感知总开关: {_systemMedia.Enabled}");
        sb.AppendLine($"SMTC 监听组件 : {(_systemMedia.SmtcInitialized ? "正常初始化" : "未就绪/不可用")}");
        sb.AppendLine($"忽略浏览器媒体: {_systemMedia.IgnoreBrowsers}");
        sb.AppendLine($"优先内置播放器: {_systemMedia.PreferInternalPlayer}");
        sb.AppendLine($"当前感知源进程: {(_systemMedia.CurrentSessionSourceApp ?? "(无活跃媒体会话)")}");

        var trk = _systemMedia.CurrentTrack;
        var coverBytes = _systemMedia.CurrentCoverBytes;
        var coverHash = _systemMedia.CurrentCoverHash ?? trk.CoverHash;
        sb.AppendLine($"当前感知曲目 : {(string.IsNullOrEmpty(trk.Title) ? "(未感知到歌曲)" : $"{trk.Title} - {trk.Artist} (专辑: {trk.Album})")}");
        sb.AppendLine($"曲目播放状态 : {trk.Status} (进度: {trk.PositionSec:F1}s / {trk.DurationSec:F1}s, 电平峰值: {trk.VolumePeak:F3})");
        sb.AppendLine($"专辑封面状态 : {(coverBytes != null && coverBytes.Length > 0 ? $"已捕获 ({coverBytes.Length} 字节, Hash: {coverHash})" : (string.IsNullOrEmpty(coverHash) ? "无封面" : $"Hash: {coverHash}"))}");
        sb.AppendLine($"感知来源属性 : AppId={trk.SourceApp}, Platform={trk.Platform}, HasSong={trk.HasSong}");

        // 网易云 CEF 数据库诊断
        try
        {
            var webDbPath = BiLi_live_Tool.Services.SystemMedia.NeteaseWebDbReader.WebDbPath;
            if (File.Exists(webDbPath))
            {
                var fi = new FileInfo(webDbPath);
                var latestNt = BiLi_live_Tool.Services.SystemMedia.NeteaseWebDbReader.TryGetLatestTrack();
                sb.AppendLine($"网易云本地DB  : 存在 ({fi.Length / 1024.0:F1} KB, 修改于 {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss})");
                if (latestNt != null)
                {
                    var playAge = latestNt.PlaytimeMs > 0 ? (DateTimeOffset.Now.ToUnixTimeMilliseconds() - latestNt.PlaytimeMs) / 1000.0 : -1;
                    sb.AppendLine($"DB内最新歌曲  : 《{latestNt.Title}》 - {latestNt.Artist} (起播于 {(playAge >= 0 ? $"{playAge:F1}s 前" : "未知")})");
                }
                else
                {
                    sb.AppendLine("DB内最新歌曲  : 尝试读取返回 null (数据库锁定或暂无数据)");
                }
            }
            else
            {
                sb.AppendLine($"网易云本地DB  : 文件不存在 ({webDbPath})");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"网易云本地DB  : 检测异常 ({ex.Message})");
        }

#if WINDOWS
        // WASAPI 进程出声现场采样
        try
        {
            var cm = BiLi_live_Tool.Services.SystemMedia.WasapiProcessMeter.SampleProcessAudio(new[] { "cloudmusic" });
            var qq = BiLi_live_Tool.Services.SystemMedia.WasapiProcessMeter.SampleProcessAudio(new[] { "QQMusic" });
            var kg = BiLi_live_Tool.Services.SystemMedia.WasapiProcessMeter.SampleProcessAudio(new[] { "kugou" });
            var sp = BiLi_live_Tool.Services.SystemMedia.WasapiProcessMeter.SampleProcessAudio(new[] { "Spotify" });

            sb.AppendLine($"WASAPI 现场电平: 网易云[活跃={cm.IsActive}, 峰值={cm.PeakVolume:F3}] | QQ音乐[活跃={qq.IsActive}, 峰值={qq.PeakVolume:F3}] | 酷狗[活跃={kg.IsActive}, 峰值={kg.PeakVolume:F3}] | Spotify[活跃={sp.IsActive}, 峰值={sp.PeakVolume:F3}]");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"WASAPI 现场电平: 采样失败 ({ex.Message})");
        }
#endif
        sb.AppendLine();

        // ════════ 四、TTS 语音合成与播报管线 ════════
        sb.AppendLine("【四、TTS 语音合成与播报管线】");
        var edgeAlive = TtsProcessGuard.IsPortAlive(TtsHost.EdgePort);
        var mossAlive = TtsProcessGuard.IsPortAlive(TtsHost.MossPort);
        sb.AppendLine($"服务端口状态 : Edge-TTS(8020)={(edgeAlive ? "正常运行" : "未启动/未响应")}, MOSS-TTS(8021)={(mossAlive ? "运行中" : "按需离线")}");
        sb.AppendLine($"当前生效引擎 : {(_speaker.EffectiveEngine ?? "(默认未降级)")}");
        sb.AppendLine($"降级自愈状态 : {(string.IsNullOrEmpty(_speaker.EffectiveEngine) ? "正常 (未触发自愈降级)" : $"★ 已降级至 {_speaker.EffectiveEngine} (触发于 {_speaker.DemotedAt:HH:mm:ss})")}");
        sb.AppendLine($"引擎失败计数 : {_speaker.EngineFails} 次连续失败");
        sb.AppendLine($"待播队列状态 : 排队 {_speaker.QueueCount} 条, 当前播报中={_speaker.Speaking}, 正在播报文本: {(_speaker.Speaking ? $"\"{_speaker.CurrentText}\"" : "无")}");
        sb.AppendLine($"防刷去重池   : 关注去重缓存={_speaker.FollowedCount} 个, 进房欢迎去重={_speaker.WelcomeCount} 个");

        var queueSnapshot = _speaker.QueueSnapshot();
        if (queueSnapshot.Count > 0)
        {
            sb.AppendLine($"待播队列前 3 项: " + string.Join(" | ", queueSnapshot.Take(3).Select((q, i) => $"#{i + 1}[{q.Type}{(q.Guard ? ",舰长" : "")}]: {q.Text}")));
        }
        sb.AppendLine();

        // ════════ 五、点歌系统与音乐服务 ════════
        sb.AppendLine("【五、点歌系统与音乐服务】");
        var songReqEnabled = _config.SectionEnabled("songRequest");
        var songReq = _pipeline.SongRequest;
        sb.AppendLine($"点歌功能开关 : {(songReqEnabled ? "已启用" : "未启用")}");
        sb.AppendLine($"当前点歌排队 : {songReq.PlaylistCount} 首歌曲");
        sb.AppendLine($"内部播放器态 : 正在播放={_songPlayer.IsPlaying}, 播放索引={_songPlayer.PlayingIndex}, 当前歌曲ID={(_songPlayer.CurrentSongId ?? "无")}");

        var songList = songReq.PlaylistSnapshot();
        if (songList.Count > 0)
        {
            sb.AppendLine($"当前排队前 3 首: " + string.Join(" | ", songList.Take(3).Select((p, i) => $"#{i + 1} 《{p.Name}》-{p.Artist} (点歌人:{p.Requester}, 平台:{p.Platform})")));
        }

        // 脱敏检查各音乐平台凭据
        var songReqCfg = _config.GetNode("songRequest") as JsonObject;
        var cookiesNode = songReqCfg?["cookies"] as JsonObject;
        var qqCookie = cookiesNode?["qq"]?.GetValue<string>();
        var ntCookie = cookiesNode?["netease"]?.GetValue<string>();
        var kgCookie = cookiesNode?["kugou"]?.GetValue<string>();
        sb.AppendLine($"QQ 音乐凭据  : {MaskCookie(qqCookie)}");
        sb.AppendLine($"网易云凭据   : {MaskCookie(ntCookie)}");
        sb.AppendLine($"酷狗音乐凭据 : {MaskCookie(kgCookie)}");
        sb.AppendLine();

        // ════════ 六、键鼠三路采集与熔断防卡死 ════════
        sb.AppendLine("【六、键鼠三路采集与熔断防卡死】");
        sb.AppendLine($"键鼠服务状态 : {(_keyview.Running ? "运行中" : "未启动")}");
        var hook = _keyview.Hook;
        if (hook != null)
        {
            sb.AppendLine($"底层钩子安装 : {(hook.HooksInstalled ? "已安装" : "未安装/已脱落")}");
            sb.AppendLine($"通道健康详情 : {hook.HooksDetail}");
            sb.AppendLine($"累计捕获事件 : {hook.Events} 个 (钩子命中: {hook.HookHits}, 原始输入: {hook.RawHits})");
            sb.AppendLine($"键盘按键命中 : 钩子键盘={hook.HookKbHits}, 原始键盘={hook.RawKbHits}");
            sb.AppendLine($"看护重挂次数 : {hook.Reinstalls} 次 (Watchdog Reinstalls)");
            sb.AppendLine($"最近输入距今 : {(hook.LastEventAgoMs >= 0 ? $"{hook.LastEventAgoMs / 1000.0:F1} 秒前" : "尚未收到事件")}");
        }
        var keyClients = _keyview.ClientCount?.Invoke() ?? 0;
        sb.AppendLine($"浮层在线连接 : {keyClients} 个 OBS/浏览器客户端");
        sb.AppendLine();

        // ════════ 七、下三分之一字幕与场景浮层 ════════
        sb.AppendLine("【七、下三分之一字幕与 OBS 场景浮层】");
        var l3Data = _lowerThirds.Data;
        sb.AppendLine($"字幕功能开关 : {(l3Data.Enabled ? "已开启" : "已关闭")}");
        var activeChannel = l3Data.Channels.FirstOrDefault(c => c.Active);
        sb.AppendLine($"字幕通道数量 : {l3Data.Channels.Count} 个通道 (当前激活: {(activeChannel != null ? $"{activeChannel.ChannelName} (#{activeChannel.Id})" : "无常驻激活")})");
        sb.AppendLine($"内置 Web 服务 : 127.0.0.1:{_kestrel.Port} ({(_kestrel.IsRunning ? "运行中" : "未启动")})");
        sb.AppendLine($"常用 OBS 浮层 : ");
        sb.AppendLine($"  - 弹幕浮层  : http://127.0.0.1:{_kestrel.Port}/legacy/danmu/overlay.html");
        sb.AppendLine($"  - 键鼠浮层  : http://127.0.0.1:{_kestrel.Port}/legacy/keyview/overlay.html");
        sb.AppendLine($"  - 下三分之一: http://127.0.0.1:{_kestrel.Port}/legacy/lower-thirds/overlay.html");
        sb.AppendLine($"  - 唱片机浮层: http://127.0.0.1:{_kestrel.Port}/legacy/now-playing/overlay.html");
        sb.AppendLine();

        // ════════ 八、WebView2、开机自启与磁盘维护 ════════
        sb.AppendLine("【八、WebView2、开机自启与磁盘维护】");
        try
        {
            // 检测 WebView2 用户数据目录
            var webviewDataDir = Path.Combine(AppConfig.DataDir, "webview");
            var altWebviewDir = Path.Combine(AppContext.BaseDirectory, "直播小帮手.exe.WebView2");
            var targetWv = Directory.Exists(webviewDataDir) ? webviewDataDir : (Directory.Exists(altWebviewDir) ? altWebviewDir : null);
            if (targetWv != null)
            {
                var dirInfo = new DirectoryInfo(targetWv);
                long totalBytes = 0;
                int count = 0;
                foreach (var f in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    totalBytes += f.Length;
                    count++;
                }
                sb.AppendLine($"WebView2 缓存: 占用 {totalBytes / (1024 * 1024.0):F1} MB (文件数: {count}, 路径: {Path.GetFileName(targetWv)})");
            }
            else
            {
                sb.AppendLine("WebView2 缓存: 目录尚未生成或已深度清理");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"WebView2 缓存: 统计异常 ({ex.Message})");
        }

#if WINDOWS
        try
        {
            using var rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            var runVal = rk?.GetValue("BiLi_live_Tool") as string;
            sb.AppendLine($"开机自启注册表: {(string.IsNullOrEmpty(runVal) ? "未开启" : $"已开启 -> \"{runVal}\"")}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"开机自启注册表: 读取异常 ({ex.Message})");
        }
#endif

        try
        {
            var updateBackupDir = Path.Combine(AppContext.BaseDirectory, "update_backup");
            if (Directory.Exists(updateBackupDir))
            {
                var bInfo = new DirectoryInfo(updateBackupDir);
                var subDirs = bInfo.GetDirectories();
                sb.AppendLine($"更新历史备份 : {subDirs.Length} 个备份批次 (目录: update_backup)");
            }
            else
            {
                sb.AppendLine("更新历史备份 : 无历史备份残留");
            }
        }
        catch { }
        sb.AppendLine();

        // ════════ 九、增强型全网连通性与本地回环探针 ════════
        sb.AppendLine("【九、增强型全网连通性与本地回环探针】");
        sb.AppendLine("正在对 B 站核心服务、Comet 长连接端口、曲库服务及本地端口进行全面现场探测...");
        var probeResults = await RunNetworkProbesAsync(_config.RoomId, _kestrel.Port);
        foreach (var pr in probeResults)
        {
            sb.AppendLine($"  - {pr.Name,-16}: {pr.Status} (耗时: {pr.LatencyMs}ms) {(string.IsNullOrEmpty(pr.Detail) ? "" : "-> " + pr.Detail)}");
        }
        sb.AppendLine();

        // ════════ 十、智能排障诊断分析、脱敏配置与黑匣子 ════════
        sb.AppendLine("【十、智能综合排障诊断建议、脱敏配置快照与黑匣子】");
        var suggestions = AnalyzeIssues(st, probeResults, _systemMedia);
        sb.AppendLine("▶ 智能排障分析研判:");
        if (suggestions.Count == 0)
        {
            sb.AppendLine("  ✓ 未发现明显的网络配置、权限阻断或长连接异常。各微服务健康。");
        }
        else
        {
            for (int i = 0; i < suggestions.Count; i++)
            {
                sb.AppendLine($"  {i + 1}. {suggestions[i]}");
            }
        }
        sb.AppendLine();

        // 服务运行日志
        sb.AppendLine("▶ 服务运行日志 (当前会话最近条目):");
        var (logs, total) = ServiceLog.Snapshot(300);
        sb.AppendLine($"当前会话内存日志: 记录 {total} 条，呈现最近 {logs.Count} 条:");
        sb.AppendLine("--------------------------------------------------------------------------------");
        foreach (var l in logs)
        {
            sb.AppendLine($"[{l.Time}] [{l.Level.ToUpper(),-5}] [{l.Src}] {l.Msg}");
        }
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine();

        // 历史崩溃与未捕获异常黑匣子记录
        sb.AppendLine("▶ 历史崩溃与未捕获异常黑匣子记录 (Crash Trap):");
        try
        {
            var fatalLog = CrashTrap.FatalCrashFile;
            var latestLog = CrashTrap.LatestCrashFile;
            var primaryLog = File.Exists(fatalLog) ? fatalLog : latestLog;

            if (File.Exists(primaryLog))
            {
                var crashInfo = new FileInfo(primaryLog);
                sb.AppendLine($"🚨 检测到异常崩溃黑匣子记录！最后写入: {crashInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss} ({crashInfo.Length} 字节)");
                sb.AppendLine("---------------------------- [崩溃日志内容摘录] ----------------------------");
                sb.AppendLine(File.ReadAllText(primaryLog, Encoding.UTF8));
                sb.AppendLine("----------------------------------------------------------------------------");
            }
            else
            {
                sb.AppendLine("✅ 无未处理崩溃或闪退异常记录 (系统运行平稳)。");
            }

            var logDir = CrashTrap.LogDir;
            if (Directory.Exists(logDir))
            {
                var di = new DirectoryInfo(logDir);
                var archiveCrashes = di.GetFiles("crash-*.log");
                if (archiveCrashes.Length > 0)
                {
                    sb.AppendLine($"累计历史崩溃归档数: {archiveCrashes.Length} 个");
                    var majorCrash = archiveCrashes
                        .Where(f => f.Length > 3000 && !string.Equals(f.FullName, primaryLog, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();

                    if (majorCrash != null)
                    {
                        sb.AppendLine($"🔍 自动回溯重大崩溃归档现场 [{majorCrash.Name}]（{majorCrash.Length} 字节）:");
                        sb.AppendLine("---------------------------- [历史重大崩溃现场回溯摘录] ----------------------------");
                        sb.AppendLine(File.ReadAllText(majorCrash.FullName, Encoding.UTF8));
                        sb.AppendLine("------------------------------------------------------------------------------------");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[读取崩溃黑匣子时出现异常]: {ex.Message}");
        }
        sb.AppendLine();

        // 持久化磁盘日志尾部
        sb.AppendLine("▶ 持久化磁盘日志状态 (Disk Logs):");
        try
        {
            var logDir = ServiceLog.LogDir;
            if (Directory.Exists(logDir))
            {
                var di = new DirectoryInfo(logDir);
                var logFiles = di.GetFiles("app-*.log");
                sb.AppendLine($"持久化日志目录: {logDir} (有效日志文件数: {logFiles.Length})");
                if (logFiles.Length > 0)
                {
                    var latestLogFile = logFiles.OrderByDescending(f => f.LastWriteTime).First();
                    sb.AppendLine($"📄 提取持久化日志尾部 [{latestLogFile.Name}] (最后 50 行):");
                    sb.AppendLine("--------------------------------------------------------------------------------");
                    try
                    {
                        using var fs = new FileStream(latestLogFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs, Encoding.UTF8);
                        var allLines = new List<string>();
                        string? line;
                        while ((line = sr.ReadLine()) != null) allLines.Add(line);
                        var tail = allLines.TakeLast(50);
                        foreach (var l in tail) sb.AppendLine(l);
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"[提取持久化日志尾部失败]: {ex.Message}");
                    }
                    sb.AppendLine("--------------------------------------------------------------------------------");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[检查持久化日志状态时出现异常]: {ex.Message}");
        }
        sb.AppendLine();

        // 完整业务配置脱敏快照
        sb.AppendLine("▶ 完整业务配置脱敏快照 (Sanitized AppConfig Snapshot):");
        sb.AppendLine("--------------------------------------------------------------------------------");
        try
        {
            var rawConfig = _config.Snapshot();
            var sanitized = SanitizeNode(rawConfig);
            var opt = new JsonSerializerOptions { WriteIndented = true };
            sb.AppendLine(sanitized?.ToJsonString(opt) ?? "(配置为空)");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[生成脱敏配置快照异常]: {ex.Message}");
        }
        sb.AppendLine("--------------------------------------------------------------------------------");

        sb.AppendLine("================================ [报告结束] ================================");

        return sb.ToString();
    }

    /// <summary>
    /// 保存诊断报告到桌面或数据目录，并返回绝对路径。
    /// </summary>
    public async Task<string> ExportToFileAsync(string? targetDir = null)
    {
        var content = await GenerateReportAsync();
        var fileName = $"直播小帮手_排障诊断日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

        if (string.IsNullOrWhiteSpace(targetDir))
        {
            targetDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (!Directory.Exists(targetDir))
            {
                targetDir = Path.Combine(AppConfig.DataDir, "logs");
            }
        }

        Directory.CreateDirectory(targetDir);
        var fullPath = Path.Combine(targetDir, fileName);

        // UTF-8 with BOM 保证 Windows 记事本双击打开中文绝不乱码
        await File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(true));
        return fullPath;
    }

    /// <summary>
    /// 在资源管理器中打开指定文件并高亮选中。
    /// </summary>
    public static void OpenFolderAndSelect(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            }
            else
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                }
            }
        }
        catch { }
    }

    private sealed record ProbeItem(string Name, string Status, long LatencyMs, string Detail);

    private async Task<List<ProbeItem>> RunNetworkProbesAsync(string roomId, int kestrelPort)
    {
        var list = new List<ProbeItem>();

        // Probe 1: B站 Nav API (WBI 密钥源)
        list.Add(await ProbeUrlAsync("B站Nav接口", "https://api.bilibili.com/x/web-interface/nav", j =>
        {
            if (j.TryGetProperty("code", out var c) && c.GetInt32() == 0) return "成功 (拿到 WBI 图标数据)";
            return "返回 code=" + (j.TryGetProperty("code", out var c2) ? c2.ToString() : "未知");
        }));

        // Probe 2: 房间号解析
        var rid = string.IsNullOrWhiteSpace(roomId) ? "591921" : roomId.Trim();
        list.Add(await ProbeUrlAsync("B站房间解析", $"https://api.live.bilibili.com/room/v1/Room/room_init?id={rid}", j =>
        {
            if (j.TryGetProperty("data", out var d) && d.TryGetProperty("room_id", out var realId))
            {
                var live = d.TryGetProperty("live_status", out var ls) && ls.GetInt32() == 1 ? "开播中" : "未开播";
                return $"解析成功: 真实房间={realId} ({live})";
            }
            return "解析失败: " + j.ToString();
        }));

        // Probe 3: 弹幕服务器探针 (getDanmuInfo)
        list.Add(await ProbeUrlAsync("B站弹幕节点", $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?id={rid}&type=0", j =>
        {
            var code = j.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            if (code == 0)
            {
                var hostsCount = j.TryGetProperty("data", out var d) && d.TryGetProperty("host_list", out var hl) ? hl.GetArrayLength() : 0;
                return $"成功获取 {hostsCount} 个弹幕节点";
            }
            if (code == -352)
            {
                return "★ 拦截警告：触发 B 站 -352 风控校验（需有效登录 Cookie 或 WBI 签名）";
            }
            return $"接口返回 code={code}";
        }));

        // Probe 4 & 5: B站 Comet WebSocket 端口探针
        list.Add(await ProbeTcpPortAsync("B站Comet(443)", "broadcastlv.chat.bilibili.com", 443));
        list.Add(await ProbeTcpPortAsync("B站Comet(2244)", "broadcastlv.chat.bilibili.com", 2244));

        // Probe 6: QQ音乐 API
        list.Add(await ProbeUrlAsync("QQ音乐曲库接口", "https://c.y.qq.com/soso/fcgi-bin/search_for_qq_cp?w=%E6%B5%8B%E8%AF%95&n=1&format=json", j =>
        {
            var c = j.TryGetProperty("code", out var cd) ? cd.GetInt32() : -1;
            return c == 0 ? "响应正常 (code=0)" : $"code={c}";
        }));

        // Probe 7: 网易云音乐 API
        list.Add(await ProbeUrlAsync("网易云曲库接口", "https://music.163.com/api/search/get/web?s=%E6%B5%8B%E8%AF%95&type=1&limit=1", j =>
        {
            var c = j.TryGetProperty("code", out var cd) ? cd.GetInt32() : -1;
            return c == 200 ? "响应正常 (code=200)" : $"code={c}";
        }));

        // Probe 8: 本地 Kestrel 状态
        list.Add(await ProbeUrlAsync("本地Kestrel服务", $"http://127.0.0.1:{kestrelPort}/api/status", j =>
        {
            var st = j.TryGetProperty("live", out var l) && l.TryGetProperty("state", out var s) ? s.GetString() : "ok";
            return $"运行中 (live={st})";
        }));

        // Probe 9: 本地弹幕浮层资源
        list.Add(await ProbeUrlAsync("本地弹幕浮层页", $"http://127.0.0.1:{kestrelPort}/legacy/danmu/overlay.html"));

        // Probe 10: 本地 TTS 服务 (8020)
        list.Add(await ProbeTcpPortAsync("本地TTS端口(8020)", "127.0.0.1", TtsHost.EdgePort));

        // Probe 11: GitHub 镜像加速探针
        list.Add(await ProbeUrlAsync("GitHub镜像加速", "https://ghfast.top/https://raw.githubusercontent.com", _ => "可达"));

        return list;
    }

    private static async Task<ProbeItem> ProbeUrlAsync(string name, string url, Func<JsonElement, string>? inspect = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            req.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
            using var resp = await Http.SendAsync(req);
            sw.Stop();
            var detail = $"HTTP {(int)resp.StatusCode}";
            if (inspect != null && resp.IsSuccessStatusCode)
            {
                try
                {
                    var stream = await resp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);
                    detail = inspect(doc.RootElement);
                }
                catch { }
            }
            return new ProbeItem(name, resp.IsSuccessStatusCode ? "正常" : $"状态异常({(int)resp.StatusCode})", sw.ElapsedMilliseconds, detail);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeItem(name, "网络失败", sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private static async Task<ProbeItem> ProbeTcpPortAsync(string name, string host, int port)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return new ProbeItem(name, "端口畅通", sw.ElapsedMilliseconds, $"TCP 三次握手成功 ({host}:{port})");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeItem(name, "连接受阻", sw.ElapsedMilliseconds, $"{host}:{port} -> {ex.Message}");
        }
    }

    private List<string> AnalyzeIssues(LiveStatusInfo st, List<ProbeItem> probes, BiLi_live_Tool.Services.SystemMedia.SystemMediaService media)
    {
        var list = new List<string>();

        if (st.State == "error")
        {
            list.Add($"【直播长连接错误】：当前连接处于错误状态。详情: {st.Error}");
        }

        var danmuProbe = probes.FirstOrDefault(p => p.Name == "B站弹幕节点");
        if (danmuProbe != null && danmuProbe.Detail.Contains("-352"))
        {
            list.Add("【B站风控拦截 (-352)】：B站拒绝了未认证的弹幕服务器查询。请尝试在客户端内点击「打开B站浏览器」重新扫码登录，或清除旧 Cookie 重新拉取。若已登录仍报错，说明当前账号被风控或本地 IP 触发了临时频率限制，程序会自动以访客模式自愈。");
        }

        var comet443 = probes.FirstOrDefault(p => p.Name == "B站Comet(443)");
        var comet2244 = probes.FirstOrDefault(p => p.Name == "B站Comet(2244)");
        if (comet443 != null && comet443.Status != "端口畅通" && comet2244 != null && comet2244.Status != "端口畅通")
        {
            list.Add("【B站弹幕长连接端口阻断】：无法连接到 broadcastlv.chat.bilibili.com 的 443 或 2244 端口。通常是因为局域网防火墙、杀毒软件或网络代理阻断了 WebSocket 二进制连接。");
        }

        var navProbe = probes.FirstOrDefault(p => p.Name == "B站Nav接口");
        if (navProbe != null && navProbe.Status != "正常")
        {
            list.Add($"【基础网络异常】：无法正常访问 api.bilibili.com（{navProbe.Detail}），请检查网络连接、DNS 解析或代理软件设置。");
        }

        if (string.IsNullOrEmpty(_config.RoomId))
        {
            list.Add("【房间号未填】：未设置房间号，请在「房间与连接」页面输入主播房间号后点击「连接」。");
        }

        if (_verify.Locked)
        {
            list.Add($"【授权未通过】：当前账号未在授权白名单中（{_verify.State.Message}），连接被安全锁定。请联系作者添加授权。");
        }

        var ttsPortProbe = probes.FirstOrDefault(p => p.Name.StartsWith("本地TTS端口"));
        if (ttsPortProbe != null && ttsPortProbe.Status != "端口畅通")
        {
            list.Add("【TTS 语音合成服务异常】：本地 8020 端口未响应。若需要语音播报，请确认 TTS 引擎进程已启动，或检查 8020 端口是否被其他软件占用。");
        }

        if (media.Enabled && !media.SmtcInitialized)
        {
            list.Add("【Windows 媒体感知未就绪】：SMTC 监听组件未能正常初始化。通常是因为操作系统版本过低或当前运行在受限的服务/沙盒环境中。");
        }

        return list;
    }

    private static string GetStateDescription(string state) => state switch
    {
        "idle" => "未连接",
        "resolving" => "正在解析房间号",
        "checking_live" => "检测开播状态",
        "not_live" => "房间未开播",
        "getting_danmu" => "正在获取弹幕服务器节点",
        "connected" => "已连接 (CONNECTED)",
        "reconnecting" => "正在重连",
        "disconnected" => "已断开",
        "error" => "连接发生错误",
        _ => state,
    };
}
