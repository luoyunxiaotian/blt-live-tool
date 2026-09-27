using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace BiLi_live_Tool.Services;

/// <summary>
/// 全局未捕获异常黑匣子（Crash Trap）：
/// 在进程因未处理异常闪退前，将完整异常类型、调用堆栈、内部异常链、环境状态与最近服务日志
/// 同步强刷到 data/logs/crash.log 与 crash-{ts}.log，保证任何崩溃均有据可查。
/// </summary>
public static class CrashTrap
{
    private static bool _initialized;
    private static readonly object WriteLock = new();

    public static string LogDir => Path.Combine(AppConfig.DataDir, "logs");
    public static string LatestCrashFile => Path.Combine(LogDir, "crash.log");

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            Directory.CreateDirectory(LogDir);
        }
        catch { }

        // 1. 全局 AppDomain 未捕获异常（涵盖所有非 UI 线程、线程池、Task 未处理异常）
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            RecordCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);
        };

        // 2. Task 调度器中未观察到的异步任务异常
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            RecordCrash("TaskScheduler.UnobservedTaskException", e.Exception, isTerminating: false);
            // 标记已观察，避免在某些 CLR 配置下直接终止进程
            e.SetObserved();
        };

        // 3. 进程正常退出前强刷日志
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            try { ServiceLog.Flush(); } catch { }
        };
    }

    /// <summary>
    /// 记录崩溃信息并强制落盘。
    /// </summary>
    public static void RecordCrash(string source, Exception? ex, bool isTerminating)
    {
        lock (WriteLock)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                var now = DateTime.Now;
                var sb = new StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine("                      直播小帮手 · 严重崩溃与未捕获异常日志");
                sb.AppendLine($"崩溃时间 : {now:yyyy-MM-dd HH:mm:ss.fff} (本地时间)");
                sb.AppendLine($"异常来源 : {source}");
                sb.AppendLine($"进程终结 : {(isTerminating ? "是 (Fatal Process Terminating)" : "否 (Non-Terminating / Recovered)")}");
                sb.AppendLine("================================================================================");
                sb.AppendLine();

                // 运行环境上下文
                var proc = Process.GetCurrentProcess();
                sb.AppendLine("【一、系统与进程环境】");
                sb.AppendLine($"客户端版本   : {KestrelHost.VersionText}");
                sb.AppendLine($"运行时框架   : {RuntimeInformation.FrameworkDescription}");
                sb.AppendLine($"操作系统     : {RuntimeInformation.OSDescription} ({(Environment.Is64BitOperatingSystem ? "64位" : "32位")})");
                sb.AppendLine($"进程 PID     : {proc.Id}");
                sb.AppendLine($"运行时间     : {(now - proc.StartTime).TotalMinutes:F2} 分钟");
                sb.AppendLine($"物理内存     : {proc.WorkingSet64 / (1024 * 1024):F1} MB (峰值: {proc.PeakWorkingSet64 / (1024 * 1024):F1} MB)");
                sb.AppendLine($"GC 堆内存    : {GC.GetTotalMemory(false) / (1024 * 1024):F1} MB");
                sb.AppendLine($"基础目录     : {AppContext.BaseDirectory}");
                sb.AppendLine($"数据目录     : {AppConfig.DataDir}");
                sb.AppendLine();

                // 异常详情与调用链
                sb.AppendLine("【二、异常详细信息与调用堆栈】");
                if (ex == null)
                {
                    sb.AppendLine("(无受管异常对象，可能为非托管宿主硬故障)");
                }
                else
                {
                    DumpException(sb, ex, level: 0);
                }
                sb.AppendLine();

                // 崩溃前最后 60 条服务日志上下文
                sb.AppendLine("【三、崩溃前服务运行日志（最后 60 条）】");
                var (recentLines, totalCount) = ServiceLog.Snapshot(60);
                sb.AppendLine($"累计日志总数: {totalCount}，呈现最近 {recentLines.Count} 条记录:");
                sb.AppendLine("--------------------------------------------------------------------------------");
                foreach (var line in recentLines)
                {
                    sb.AppendLine($"[{line.Time}] [{line.Level.ToUpperInvariant(),-5}] [{line.Src}] {line.Msg}");
                }
                sb.AppendLine("================================================================================");

                var content = sb.ToString();

                // 1. 同步覆盖写入最新 crash.log（方便一键提取）
                File.WriteAllText(LatestCrashFile, content, Encoding.UTF8);

                // 2. 同时生成一份带时间戳的历史归档文件（防止下次启动覆盖）
                var archiveFile = Path.Combine(LogDir, $"crash-{now:yyyyMMdd-HHmmss}.log");
                File.WriteAllText(archiveFile, content, Encoding.UTF8);

                // 3. 同时把致命异常同步给 ServiceLog
                try
                {
                    ServiceLog.Error("崩溃", $"[{source}] {(ex != null ? ex.GetType().Name + ": " + ex.Message : "未知异常")}");
                    ServiceLog.Flush();
                }
                catch { }
            }
            catch
            {
                // 崩溃捕获器本身决不能再次抛出任何异常
            }
        }
    }

    private static void DumpException(StringBuilder sb, Exception ex, int level)
    {
        var indent = new string(' ', level * 2);
        sb.AppendLine($"{indent}异常类型: {ex.GetType().FullName}");
        sb.AppendLine($"{indent}错误信息: {ex.Message}");
        sb.AppendLine($"{indent}HResult : 0x{ex.HResult:X8}");
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            sb.AppendLine($"{indent}调用堆栈:");
            sb.AppendLine(ex.StackTrace);
        }

        if (ex is AggregateException agg)
        {
            int idx = 1;
            foreach (var inner in agg.InnerExceptions)
            {
                sb.AppendLine($"{indent}--> 聚合内部异常 #{idx++}:");
                DumpException(sb, inner, level + 1);
            }
        }
        else if (ex.InnerException != null)
        {
            sb.AppendLine($"{indent}--> 内部异常 (InnerException):");
            DumpException(sb, ex.InnerException, level + 1);
        }
    }
}
