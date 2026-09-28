using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiLi_live_Tool.Services;

/// <summary>
/// 生成运行环境、状态探测、业务配置（脱敏）与服务日志的综合诊断报告，
/// 便于用户一键导出反馈给开发者排查问题。
/// </summary>
public sealed class DiagnosticService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly Regex CookieMaskRegex = new(@"(SESSDATA|bili_jct|MUSIC_U|qm_keyst|token)=([^;]+)", RegexOptions.Compiled);

    private readonly AppConfig _config;
    private readonly EventHub _hub;
    private readonly LiveService _live;
    private readonly VerifyService _verify;
    private readonly KestrelHost _kestrel;
    private readonly TtsHost _tts;
    private readonly KeyViewService _keyview;

    public DiagnosticService(
        AppConfig config,
        EventHub hub,
        LiveService live,
        VerifyService verify,
        KestrelHost kestrel,
        TtsHost tts,
        KeyViewService keyview)
    {
        _config = config;
        _hub = hub;
        _live = live;
        _verify = verify;
        _kestrel = kestrel;
        _tts = tts;
        _keyview = keyview;
    }

    /// <summary>
    /// 脱敏 Cookie：对敏感的 SESSDATA/bili_jct 等值进行掩码处理，保留前4位与长度，防止泄露登录凭据。
    /// </summary>
    public static string MaskCookie(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(未配置 Cookie)";
        return CookieMaskRegex.Replace(raw, m =>
        {
            var key = m.Groups[1].Value;
            var val = m.Groups[2].Value.Trim();
            if (val.Length <= 8) return $"{key}=***";
            return $"{key}={val[..4]}***{val[^4..]} (长度:{val.Length})";
        });
    }

    /// <summary>
    /// 生成全量诊断报告文本（Markdown 纯文本结构）。
    /// </summary>
    public async Task<string> GenerateReportAsync()
    {
        var sb = new StringBuilder();
        var now = DateTime.Now;
        var proc = Process.GetCurrentProcess();

        sb.AppendLine("================================================================================");
        sb.AppendLine("                 直播小帮手 · 客户端运行环境与诊断排障日志");
        sb.AppendLine($"生成时间: {now:yyyy-MM-dd HH:mm:ss} (本地时间)");
        sb.AppendLine("脱敏声明: 本日志已自动对用户私密凭据（Cookie/Token）执行掩码脱敏，可安全分享排障。");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        // ---- 一、系统与运行环境 ----
        sb.AppendLine("【一、系统与运行环境】");
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
        sb.AppendLine($"布局标记文件 : {(File.Exists(layoutPath) ? "存在 (layout.json 安装包模式)" : "无 (便携绿色模式)")}");
        sb.AppendLine();

        // ---- 二、直播间连接与 B 站状态 ----
        var st = _hub.LastStatus;
        sb.AppendLine("【二、直播间连接与 B 站账号状态】");
        sb.AppendLine($"配置直播间号 : {(string.IsNullOrEmpty(_config.RoomId) ? "(未设置)" : _config.RoomId)}");
        sb.AppendLine($"解析真实房间 : {(string.IsNullOrEmpty(st.RealRoomId) ? "(未解析)" : st.RealRoomId)}");
        sb.AppendLine($"主播 UID     : {(string.IsNullOrEmpty(st.AnchorUid) ? "(未知)" : st.AnchorUid)}");
        sb.AppendLine($"当前连接状态 : {st.State} (Text: {GetStateDescription(st.State)})");
        sb.AppendLine($"直播间标题   : {(string.IsNullOrEmpty(st.Title) ? "—" : st.Title)}");
        sb.AppendLine($"最近连接时间 : {(st.ConnectedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(st.ConnectedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "未处于连接态")}");
        sb.AppendLine($"状态错误信息 : {(string.IsNullOrEmpty(st.Error) ? "(无错误)" : "★ " + st.Error)}");
        sb.AppendLine($"自动重连开关 : {_config.AutoConnect}");
        sb.AppendLine($"当前登录 UID : {(string.IsNullOrEmpty(_config.Uid) ? "(未登录)" : _config.Uid)}");
        sb.AppendLine($"Cookie 状态  : {MaskCookie(_config.Cookie)}");
        sb.AppendLine($"白名单授权态 : Checked={_verify.State.Checked}, Locked={_verify.Locked}, Code={_verify.State.Code}, UID={_verify.State.Uid}, Name={_verify.State.Name}");
        sb.AppendLine();

        // ---- 三、内部服务与端口 ----
        sb.AppendLine("【三、内部模块与网络端口状态】");
        sb.AppendLine($"进程内 Kestrel: {(_kestrel.IsRunning ? "运行中" : "未启动")} (端口: {_kestrel.Port})");
        sb.AppendLine($"Kestrel 错误 : {(_kestrel.LastError ?? "无")}");
        sb.AppendLine($"TTS 进程状态 : {(TtsProcessGuard.IsPortAlive(TtsHost.EdgePort) ? "运行中 (8020)" : "未启动")}");
        sb.AppendLine($"键鼠监控状态 : {(_keyview.Running ? "运行中" : "未启动")}, 钩子重挂次数={_keyview.HookReinstalls}");
        sb.AppendLine();

        // ---- 四、现场网络与 B 站 API 连通性测试 ----
        sb.AppendLine("【四、现场网络与 B 站接口探测】");
        sb.AppendLine("正在对 B 站核心服务与 CDN 进行现场连通性探测...");
        var probeResults = await RunNetworkProbesAsync(_config.RoomId);
        foreach (var pr in probeResults)
        {
            sb.AppendLine($"  - {pr.Name,-16}: {pr.Status} (耗时: {pr.LatencyMs}ms) {(string.IsNullOrEmpty(pr.Detail) ? "" : "-> " + pr.Detail)}");
        }
        sb.AppendLine();

        // ---- 五、智能排障建议 ----
        sb.AppendLine("【五、智能诊断分析与建议】");
        var suggestions = AnalyzeIssues(st, probeResults);
        if (suggestions.Count == 0)
        {
            sb.AppendLine("  ✓ 未发现明显的网络配置或服务阻断异常。");
        }
        else
        {
            for (int i = 0; i < suggestions.Count; i++)
            {
                sb.AppendLine($"  {i + 1}. {suggestions[i]}");
            }
        }
        sb.AppendLine();

        // ---- 六、服务运行日志（当前会话） ----
        sb.AppendLine("【六、服务运行日志 (当前会话最近条目)】");
        var (logs, total) = ServiceLog.Snapshot(300);
        sb.AppendLine($"当前会话日志: 内存记录 {total} 条，呈现最近 {logs.Count} 条:");
        sb.AppendLine("--------------------------------------------------------------------------------");
        foreach (var l in logs)
        {
            sb.AppendLine($"[{l.Time}] [{l.Level.ToUpper(),-5}] [{l.Src}] {l.Msg}");
        }
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine();

        // ---- 七、历史崩溃与未捕获异常黑匣子记录 ----
        sb.AppendLine("【七、历史崩溃与未捕获异常黑匣子记录 (Crash Trap)】");
        try
        {
            var fatalLog = CrashTrap.FatalCrashFile;
            var latestLog = CrashTrap.LatestCrashFile;
            var primaryLog = File.Exists(fatalLog) ? fatalLog : latestLog;

            if (File.Exists(primaryLog))
            {
                var crashInfo = new FileInfo(primaryLog);
                sb.AppendLine($"🚨 检测到严重崩溃/异常黑匣子记录！");
                sb.AppendLine($"崩溃日志路径: {primaryLog}");
                sb.AppendLine($"最后写入时间: {crashInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"日志文件大小: {crashInfo.Length} 字节");
                sb.AppendLine("---------------------------- [崩溃日志内容摘录] ----------------------------");
                sb.AppendLine(File.ReadAllText(primaryLog, Encoding.UTF8));
                sb.AppendLine("----------------------------------------------------------------------------");
            }
            else
            {
                sb.AppendLine("✅ 无未处理崩溃或闪退异常记录 (系统运行记录正常)。");
            }

            // 列出历史归档的 crash 列表
            var logDir = CrashTrap.LogDir;
            if (Directory.Exists(logDir))
            {
                var di = new DirectoryInfo(logDir);
                var archiveCrashes = di.GetFiles("crash-*.log");
                if (archiveCrashes.Length > 0)
                {
                    sb.AppendLine($"累计历史崩溃归档数: {archiveCrashes.Length} 个");
                    foreach (var cf in archiveCrashes)
                    {
                        sb.AppendLine($"  - {cf.Name} ({cf.Length} 字节, {cf.LastWriteTime:yyyy-MM-dd HH:mm:ss})");
                    }

                    // 🚨 智能历史大崩溃回溯：若历史归档中存在包含丰富上下文（>3000字节）的崩溃日志且非当前已展示的文件，
                    // 自动展开最近的一份，绝不让重启后的轻量警告日志掩盖真实的崩溃现场！
                    var majorCrash = archiveCrashes
                        .Where(f => f.Length > 3000 && !string.Equals(f.FullName, primaryLog, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();

                    if (majorCrash != null)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"🔍 自动回溯：检出历史重大崩溃归档现场 [{majorCrash.Name}]（{majorCrash.Length} 字节），为您展开回溯分析：");
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

        // ---- 八、持久化磁盘日志文件状态 ----
        sb.AppendLine("【八、持久化磁盘日志文件状态 (Disk Logs)】");
        try
        {
            var logDir = ServiceLog.LogDir;
            if (Directory.Exists(logDir))
            {
                var di = new DirectoryInfo(logDir);
                var logFiles = di.GetFiles("app-*.log");
                sb.AppendLine($"持久化日志目录: {logDir}");
                sb.AppendLine($"有效日志文件数: {logFiles.Length} 个");
                foreach (var lf in logFiles)
                {
                    sb.AppendLine($"  - {lf.Name} ({lf.Length / 1024.0:F1} KB, 最后修改: {lf.LastWriteTime:yyyy-MM-dd HH:mm:ss})");
                }

                if (logFiles.Length > 0)
                {
                    var latestLogFile = logFiles.OrderByDescending(f => f.LastWriteTime).First();
                    sb.AppendLine();
                    sb.AppendLine($"📄 自动提取磁盘持久化日志尾部 [{latestLogFile.Name}]（最后 50 行，用于排查卡死或重启前的现场业务）:");
                    sb.AppendLine("--------------------------------------------------------------------------------");
                    try
                    {
                        using var fs = new FileStream(latestLogFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var sr = new StreamReader(fs, Encoding.UTF8);
                        var allLines = new List<string>();
                        string? line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            allLines.Add(line);
                        }
                        var tail = allLines.TakeLast(50);
                        foreach (var l in tail)
                        {
                            sb.AppendLine(l);
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"[提取持久化日志尾部失败]: {ex.Message}");
                    }
                    sb.AppendLine("--------------------------------------------------------------------------------");
                }
            }
            else
            {
                sb.AppendLine($"日志目录尚未创建或为空: {logDir}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[检查持久化日志状态时出现异常]: {ex.Message}");
        }

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

    private async Task<List<ProbeItem>> RunNetworkProbesAsync(string roomId)
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

        // Probe 4: GitHub 镜像加速探针
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

    private List<string> AnalyzeIssues(LiveStatusInfo st, List<ProbeItem> probes)
    {
        var list = new List<string>();

        if (st.State == "error")
        {
            list.Add($"当前直播连接处于【错误】状态。错误信息: {st.Error}");
        }

        var danmuProbe = probes.FirstOrDefault(p => p.Name == "B站弹幕节点");
        if (danmuProbe != null && danmuProbe.Detail.Contains("-352"))
        {
            list.Add("【B站风控拦截 (-352)】：B站拒绝了未认证的弹幕服务器查询。请尝试在客户端内点击「B站登录」重新扫码登录，或导入包含有效 SESSDATA 的 Cookie。如果已登录仍报错，说明当前 Cookie 已过期或本地 IP 触发了临时频率限制。");
        }

        var navProbe = probes.FirstOrDefault(p => p.Name == "B站Nav接口");
        if (navProbe != null && navProbe.Status != "正常")
        {
            list.Add($"【基础网络异常】：无法正常访问 api.bilibili.com（{navProbe.Detail}），请检查网络连接、DNS 解析或代理软件设置。");
        }

        if (string.IsNullOrEmpty(_config.RoomId))
        {
            list.Add("【房间号未填】：未设置房间号，请在上方输入框输入主播房间号后点击「连接」。");
        }

        if (_verify.Locked)
        {
            list.Add($"【授权未通过】：当前账号未在授权白名单中（{_verify.State.Message}），连接被安全锁定。请联系作者添加授权。");
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
