// CleanupService.cs — removes files a newer release no longer ships ("obsolete"), on demand.
//
// Why: the in-app updater can only add/overwrite files (robocopy /E), so a release that drops
// or renames files would leave the old ones behind forever. The release manifest therefore
// carries a cumulative `obsolete` list (every path dropped since the layout baseline, minus the
// paths the new release ships again), and this service deletes exactly those — from the UI
// button, never automatically. The publish step also writes a matching .cmd for manual use.
//
// Safety rails (deliberately strict):
//   * only paths that appear literally in the list are touched — no wildcards, no directory scans;
//   * a path must resolve inside the app folder (manifest paths are exe-relative) after
//     normalisation, so "..\" escapes are rejected;
//   * the protected set (user data, updater state, browser profile, launcher, readme) is never
//     deleted even if it shows up in a list;
//   * each file is copied into update_backup\cleanup-<timestamp>\ first, so a mistake is recoverable;
//   * skipped files (locked by a running process) stay pending for the next attempt and are
//     reported by name.

using System.Text;
using System.Text.Json.Nodes;

namespace BiLi_live_Tool.Services;

public sealed class CleanupService
{
    public sealed record Report(int Deleted, int Missing, int Skipped, string BackupDir, List<string> SkippedPaths, string LogPath)
    {
        public string Summary => Skipped == 0
            ? $"已清理 {Deleted} 个旧文件" + (Missing > 0 ? $"（另有 {Missing} 个本来就不存在）" : "")
            : $"已清理 {Deleted} 个，{Skipped} 个被占用已跳过（下次可再试）";
    }

    private readonly object _lock = new();
    private readonly List<string> _pending = new();
    private string _pendingVersion = "";

    private static string PendingPath => Path.Combine(AppConfig.DataDir, "pending-cleanup.json");
    private static string StatePath => Path.Combine(AppConfig.DataDir, "cleanup-state.json");
    private static string LogPath => Path.Combine(AppConfig.DataDir, "cleanup-log.txt");
    private static string BackupRoot => Path.Combine(AppContext.BaseDirectory, "update_backup");

    /// <summary>Never deleted, even if a (buggy) list mentions them. Compared on the first
    /// path segment, case-insensitively.</summary>
    private static readonly string[] Protected =
    {
        "data", "update_staging", "update_backup", "config.json", "layout.json",
        "说明.txt", "BiLi_live_Tool.exe.WebView2", "直播小帮手.exe", "cleanup",
    };

    public CleanupService()
    {
        Load();
    }

    public int PendingCount { get { lock (_lock) return _pending.Count; } }
    public string PendingVersion { get { lock (_lock) return _pendingVersion; } }

    public object Status()
    {
        string lastCleaned = "", lastAt = "";
        try
        {
            if (File.Exists(StatePath) && JsonNode.Parse(File.ReadAllText(StatePath)) is JsonObject st)
            {
                lastCleaned = st["version"]?.GetValue<string>() ?? "";
                lastAt = st["at"]?.GetValue<string>() ?? "";
            }
        }
        catch { }
        lock (_lock)
            return new
            {
                pending = _pending.Count,
                pendingVersion = _pendingVersion,
                lastCleanedVersion = lastCleaned,
                lastCleanedAt = lastAt,
                appDir = AppContext.BaseDirectory,
                protectedHint = "只删除清单内的旧文件；data\\、配置与浏览器数据不会被碰，删除前会备份到 update_backup\\cleanup-*",
            };
    }

    /// <summary>Merge the obsolete paths advertised by a release manifest into the pending list.</summary>
    public void Merge(IEnumerable<string>? paths, string version)
    {
        if (paths == null) return;
        try
        {
            var added = 0;
            lock (_lock)
            {
                foreach (var raw in paths)
                {
                    var p = (raw ?? "").Trim();
                    if (p.Length == 0) continue;
                    if (_pending.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) continue;
                    _pending.Add(p);
                    added++;
                }
                if (version.Length > 0) _pendingVersion = version;
            }
            if (added > 0) Save();
        }
        catch { }
    }

    /// <summary>Delete the pending obsolete files. Returns a report; nothing throws.</summary>
    public Report Run()
    {
        List<string> todo;
        lock (_lock) todo = _pending.ToList();

        var backupDir = Path.Combine(BackupRoot, "cleanup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var appDir = Path.GetFullPath(AppContext.BaseDirectory);
        int deleted = 0, missing = 0;
        var skipped = new List<string>();
        var log = new StringBuilder();
        log.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 清理旧文件（清单 {todo.Count} 条，来源版本 {_pendingVersion}）====");

        foreach (var rel in todo)
        {
            string target;
            try
            {
                // 只剥掉 "./" 或 "/" 前缀；含 ".." 的一律拒绝 —— 不做静默规范化：清单路径由我们自己
                // 生成，出现越界说明清单异常，应当报出来而不是悄悄改写成安全路径
                var clean = rel.Replace('\\', '/');
                while (clean.StartsWith("./", StringComparison.Ordinal)) clean = clean[2..];
                clean = clean.TrimStart('/');
                if (clean.Length == 0 || clean.Split('/').Any(s => s == ".."))
                {
                    log.AppendLine($"  拒绝（路径可疑）: {rel}");
                    continue;
                }
                var first = clean.Split('/')[0];
                if (Protected.Any(p => string.Equals(p, first, StringComparison.OrdinalIgnoreCase)))
                {
                    log.AppendLine($"  拒绝（保护名单）: {rel}");
                    continue;
                }
                target = Path.GetFullPath(Path.Combine(appDir, clean.Replace('/', Path.DirectorySeparatorChar)));
                // 必须落在程序目录内（清单路径是 exe 相对；这条挡住 "..\" 逃逸与绝对路径）
                if (!target.StartsWith(appDir, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(target, appDir, StringComparison.OrdinalIgnoreCase))
                {
                    log.AppendLine($"  拒绝（越出程序目录）: {rel}");
                    continue;
                }
            }
            catch (Exception e)
            {
                log.AppendLine($"  拒绝（路径无效）: {rel} — {e.Message}");
                continue;
            }

            var isDir = Directory.Exists(target);
            var isFile = File.Exists(target);
            if (!isDir && !isFile) { missing++; continue; }

            try
            {
                // 备份后再删：出问题可从这里拿回
                var backup = Path.Combine(backupDir, Path.GetRelativePath(appDir, target));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                if (isDir)
                {
                    CopyDir(target, backup);
                    Directory.Delete(target, recursive: true);
                }
                else
                {
                    File.Copy(target, backup, overwrite: true);
                    File.Delete(target);
                }
                deleted++;
                log.AppendLine($"  已删除: {rel}");
            }
            catch (Exception e)
            {
                // 被占用/无权限：保留在待清理列表里，下次再试
                skipped.Add(rel);
                log.AppendLine($"  跳过（可能被占用）: {rel} — {e.GetType().Name}: {e.Message}");
            }
        }

        lock (_lock)
        {
            _pending.Clear();
            _pending.AddRange(skipped);
        }
        Save();

        try
        {
            Directory.CreateDirectory(AppConfig.DataDir);
            File.AppendAllText(LogPath, log.ToString(), Encoding.UTF8);
            File.WriteAllText(StatePath, new JsonObject
            {
                ["version"] = _pendingVersion,
                ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["deleted"] = deleted,
                ["missing"] = missing,
                ["skipped"] = skipped.Count,
            }.ToJsonString(), Encoding.UTF8);
        }
        catch { }

        // 空目录顺手收尾（只清空的父目录，不动 data）
        try { if (Directory.Exists(backupDir) && !Directory.EnumerateFileSystemEntries(backupDir).Any()) Directory.Delete(backupDir); } catch { }
        try { PruneEmptyDirs(appDir); } catch { }

        return new Report(deleted, missing, skipped.Count, Directory.Exists(backupDir) ? backupDir : "", skipped, LogPath);
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var to = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to, overwrite: true);
        }
    }

    /// <summary>Removes directories that became empty after cleanup (never the app root).</summary>
    private static void PruneEmptyDirs(string root)
    {
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                var first = Path.GetRelativePath(root, dir).Split(Path.DirectorySeparatorChar)[0];
                if (Protected.Any(p => string.Equals(p, first, StringComparison.OrdinalIgnoreCase))) continue;
                if (string.Equals(dir, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(PendingPath)) return;
            if (JsonNode.Parse(File.ReadAllText(PendingPath)) is not JsonObject o) return;
            _pendingVersion = o["version"]?.GetValue<string>() ?? "";
            if (o["paths"] is JsonArray arr)
                foreach (var n in arr)
                {
                    var p = n?.GetValue<string>() ?? "";
                    if (p.Length > 0 && !_pending.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))
                        _pending.Add(p);
                }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDir);
            var arr = new JsonArray();
            lock (_lock)
                foreach (var p in _pending) arr.Add(p);
            File.WriteAllText(PendingPath, new JsonObject
            {
                ["version"] = _pendingVersion,
                ["paths"] = arr,
            }.ToJsonString(), Encoding.UTF8);
        }
        catch { }
    }
}
