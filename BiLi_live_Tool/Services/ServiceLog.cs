using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BiLi_live_Tool.Services;

/// <summary>
/// 双通道企业级服务日志引擎（Dual-Sink Service Logger）：
/// 1. 内存通道：保持 600 行轻量环形缓冲，向前端 /api/maui/server/log 实时输出；
/// 2. 磁盘通道：强刷落盘至 data/logs/app-yyyyMMdd.log，支持自动按日轮转与 7 天保留策略，
///    配合 FileShare.ReadWrite 与 AutoFlush，即便进程遭遇闪退异常，关键上下文也不会丢失。
/// </summary>
public static class ServiceLog
{
    public sealed record Line(string Time, string Level, string Src, string Msg);

    private const int Cap = 600;
    private static readonly object Gate = new();
    private static readonly LinkedList<Line> Buf = new();

    // 磁盘持久化通道状态
    private static string? _logDir;
    private static string _currentDay = "";
    private static StreamWriter? _diskWriter;
    private static bool _pruneCheckedToday;

    public static string LogDir
    {
        get
        {
            if (_logDir != null) return _logDir;
            _logDir = Path.Combine(AppConfig.DataDir, "logs");
            return _logDir;
        }
    }

    public static void Write(string level, string src, string msg)
    {
        var now = DateTime.Now;
        var timeStr = now.ToString("HH:mm:ss");
        var fullTimeStr = now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        lock (Gate)
        {
            // 1. 内存通道写入
            Buf.AddLast(new Line(timeStr, level, src, msg));
            while (Buf.Count > Cap) Buf.RemoveFirst();

            // 2. 磁盘持久化通道写入
            try
            {
                EnsureDiskWriter(now);
                if (_diskWriter != null)
                {
                    _diskWriter.WriteLine($"[{fullTimeStr}] [{level.ToUpperInvariant(),-5}] [{src}] {msg}");
                }
            }
            catch
            {
                // 日志磁盘写入异常绝对不能向上抛出影响业务
            }
        }
    }

    public static void Info(string src, string msg) => Write("info", src, msg);
    public static void Warn(string src, string msg) => Write("warn", src, msg);
    public static void Error(string src, string msg) => Write("error", src, msg);

    public static (List<Line> Lines, int Count) Snapshot(int max = 300)
    {
        lock (Gate)
        {
            var all = new List<Line>(Buf);
            var tail = all.Count > max ? all.GetRange(all.Count - max, max) : all;
            return (tail, Buf.Count);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Buf.Clear();
            Info("系统", "服务运行日志已手动清空");
        }
    }

    /// <summary>
    /// 强制将当前磁盘写入缓冲刷到物理磁盘。
    /// </summary>
    public static void Flush()
    {
        lock (Gate)
        {
            try
            {
                _diskWriter?.Flush();
            }
            catch { }
        }
    }

    private static void EnsureDiskWriter(DateTime now)
    {
        var day = now.ToString("yyyyMMdd");
        if (_diskWriter != null && _currentDay == day) return;

        try
        {
            _diskWriter?.Dispose();
            _diskWriter = null;

            Directory.CreateDirectory(LogDir);
            var logPath = Path.Combine(LogDir, $"app-{day}.log");
            var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _diskWriter = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
            _currentDay = day;

            if (!_pruneCheckedToday)
            {
                _pruneCheckedToday = true;
                PruneOldLogs(LogDir);
            }
        }
        catch
        {
            _diskWriter = null;
        }
    }

    /// <summary>
    /// 日志定期清理：自动清理 7 天前的历史日切日志，避免长期挂机无限制累积磁盘空间。
    /// </summary>
    private static void PruneOldLogs(string dir)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            var di = new DirectoryInfo(dir);
            if (!di.Exists) return;

            foreach (var fi in di.GetFiles("app-*.log"))
            {
                if (fi.LastWriteTime < cutoff)
                {
                    try { fi.Delete(); } catch { }
                }
            }

            // 保留最近 10 个 crash 日志，多余的清理
            var crashFiles = di.GetFiles("crash-*.log");
            if (crashFiles.Length > 10)
            {
                Array.Sort(crashFiles, (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                for (int i = 10; i < crashFiles.Length; i++)
                {
                    try { crashFiles[i].Delete(); } catch { }
                }
            }
        }
        catch { }
    }
}
