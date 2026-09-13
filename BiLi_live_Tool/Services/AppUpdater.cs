using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace BiLi_live_Tool.Services;

/// <summary>
/// In-app updater. Prefers an incremental update: fetch the release manifest,
/// hash the installed files, and download only the changed files (the patch
/// package). Falls back to the full package when the release has no manifest,
/// no matching patch (version skipped), or anything goes wrong.
///
/// Flow:
///   [Incremental] manifest → local diff → patch only → verify each file → payload
///   [Full]        download zip → verify size/SHA256 → extract → payload
///   → Staged (apply-update.cmd written)
///   → ApplyAndRestart(): stop TTS/browser/recording → spawn script with our PID
///     → script waits for exit → backup → robocopy payload over the app dir
///     (keeping config.json + data/) → restart → write result.txt
/// </summary>
public sealed class AppUpdater
{
    public enum Phase { Idle, Downloading, Verifying, Extracting, Staged, Applying, Failed, UpToDate }

    public sealed record Status(
        Phase Phase, int Percent, string Message,
        long ReceivedBytes, long TotalBytes, string StagedVersion, bool HasResult, string ResultText,
        bool Incremental = false, int SkippedFiles = 0, int ChangedFiles = 0);

    private sealed record ManifestFile(string Path, long Size, string Sha256);
    private sealed record PatchInfo(string Name, long Size, string Sha256, string From, HashSet<string> Paths);
    private sealed record Manifest(string Version, List<ManifestFile> Files, PatchInfo? Patch);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly AppConfig _config;
    private readonly string _currentVersion;
    private readonly object _lock = new();
    private Status _status = new(Phase.Idle, 0, "", 0, 0, "", false, "");
    private CancellationTokenSource? _cts;

    public AppUpdater(AppConfig config, string currentVersion)
    {
        _config = config;
        _currentVersion = currentVersion.Trim();
    }

    public event Action? Changed;

    public Status Current { get { lock (_lock) return _status; } }

    public string StagingDir => Path.Combine(AppContext.BaseDirectory, "update_staging");
    public string BackupDir => Path.Combine(AppContext.BaseDirectory, "update_backup");
    private string PayloadDir => Path.Combine(StagingDir, "payload");
    private string ApplyScript => Path.Combine(StagingDir, "apply-update.cmd");
    private string ResultFile => Path.Combine(StagingDir, "result.txt");

    /// <summary>
    /// Stages the given release. <paramref name="manifestUrl"/> enables the
    /// incremental path; <paramref name="assetUrl"/> is the full package (also the
    /// base URL used to locate the patch beside it).
    /// </summary>
    public async Task<Status> DownloadAndStageAsync(
        string assetUrl, string fileName, long expectedSize, string expectedSha, string version,
        string manifestUrl = "", CancellationToken ct = default)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        try
        {
            Directory.CreateDirectory(StagingDir);
            TryDelete(PayloadDir);
            TryDelete(ApplyScript);
            TryDelete(ResultFile);

            // ---- 1) 增量路径：有清单就对比本机文件，只下变化的部分 ----
            if (!string.IsNullOrWhiteSpace(manifestUrl))
            {
                var manifest = await TryFetchManifestAsync(manifestUrl, token).ConfigureAwait(false);
                if (manifest != null)
                {
                    Set(new Status(Phase.Verifying, 0, $"正在比对清单（{manifest.Files.Count} 个文件）…", 0, 0, "", false, ""));
                    var diff = await Task.Run(() => ComputeDiff(manifest), token).ConfigureAwait(false);
                    if (diff.Count == 0)
                    {
                        Set(new Status(Phase.UpToDate, 100,
                            $"已是最新版本（逐文件比对无差异，共 {manifest.Files.Count} 个文件）", 0, 0, "", false, ""));
                        return Current;
                    }
                    var patch = manifest.Patch;
                    var patchUrl = patch != null && patch.Name.Length > 0 ? ResolveSiblingUrl(assetUrl, patch.Name) : "";
                    if (patch != null && Norm(patch.From) == Norm(_currentVersion) && patchUrl.Length > 0
                        && diff.All(d => patch.Paths.Contains(d.Path)))
                    {
                        var skipped = manifest.Files.Count - diff.Count;
                        Set(new Status(Phase.Downloading, 0,
                            $"增量更新：{diff.Count} 个文件变化，跳过 {skipped} 个未变化文件", 0, patch.Size, "", false, "", true, skipped, diff.Count));
                        var ok = await DownloadToFileAsync(patchUrl, patch.Name, patch.Size, patch.Sha256, token, "增量包",
                                (pct, got, total) => Set(new Status(Phase.Downloading, pct,
                                    $"正在下载增量包（{Mb(got)} / {Mb(total)} MB，{diff.Count} 个文件）", got, total, "", false, "", true, skipped, diff.Count)))
                            .ConfigureAwait(false);
                        if (!ok) return FallbackToFull(assetUrl, fileName, expectedSize, expectedSha, version, "增量包不可用", token);

                        Set(new Status(Phase.Extracting, 100, "正在解压增量包…", 0, 0, "", false, "", true, skipped, diff.Count));
                        await Task.Run(() => ExtractZip(Path.Combine(StagingDir, Sanitize(patch.Name) + ".part"), PayloadDir, skipPatchJson: true), token).ConfigureAwait(false);
                        var verify = await Task.Run(() => VerifyPayload(diff), token).ConfigureAwait(false);
                        if (!verify.ok)
                            return FallbackToFull(assetUrl, fileName, expectedSize, expectedSha, version, verify.message, token);

                        TryDelete(Path.Combine(StagingDir, Sanitize(patch.Name) + ".part"));
                        WriteApplyScript();
                        Set(new Status(Phase.Staged, 100,
                            $"已就绪（增量）：{version} · {diff.Count} 个文件，跳过 {skipped} 个未变化文件",
                            patch.Size, patch.Size, version, false, "", true, skipped, diff.Count));
                        return Current;
                    }
                    // 有清单但没有适用的增量包（跨版本升级 / 首次带清单的版本）→ 整包
                    Set(new Status(Phase.Downloading, 0,
                        $"增量包不适用（本机 {_currentVersion}，增量包基于 {patch?.From ?? "未知"}）→ 改为下载完整包", 0, 0, "", false, ""));
                }
            }

            // ---- 2) 整包路径 ----
            var staged = await StageFullAsync(assetUrl, fileName, expectedSize, expectedSha, version, token).ConfigureAwait(false);
            return staged;
        }
        catch (OperationCanceledException)
        {
            return Fail("已取消下载（当前版本不受影响）");
        }
        catch (Exception ex)
        {
            return Fail("更新失败：" + ex.Message + "（当前版本不受影响）");
        }
    }

    private async Task<Status> StageFullAsync(string url, string fileName, long expectedSize, string expectedSha, string version, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url)) return Fail("更新地址为空");
        var safeName = Sanitize(fileName.Length > 0 ? fileName : "update.zip");
        var zipPath = Path.Combine(StagingDir, safeName + ".part");
        var ok = await DownloadToFileAsync(url, fileName, expectedSize, expectedSha, token, "完整包",
            (pct, got, total) => Set(new Status(Phase.Downloading, pct,
                $"正在下载完整包（{Mb(got)} / {Mb(total)} MB）", got, total, "", false, ""))).ConfigureAwait(false);
        if (!ok) return Current;

        Set(new Status(Phase.Extracting, 100, "正在解压…", 0, 0, "", false, ""));
        await Task.Run(() => ExtractZip(zipPath, PayloadDir, skipPatchJson: false), token).ConfigureAwait(false);
        TryDelete(zipPath);
        var payloadFiles = Directory.Exists(PayloadDir) ? Directory.GetFiles(PayloadDir, "*", SearchOption.AllDirectories).Length : 0;
        if (payloadFiles == 0) return Fail("安装包内容为空：已丢弃，当前版本不受影响");
        WriteApplyScript();
        Set(new Status(Phase.Staged, 100, $"已就绪（完整包）：{version}（{payloadFiles} 个文件）· 点「更新并重启」完成安装",
            0, 0, version, false, ""));
        return Current;
    }

    private Status FallbackToFull(string assetUrl, string fileName, long size, string sha, string version, string why, CancellationToken token)
    {
        Set(new Status(Phase.Downloading, 0, $"{why} → 改为下载完整包", 0, 0, "", false, ""));
        return StageFullAsync(assetUrl, fileName, size, sha, version, token).GetAwaiter().GetResult();
    }

    /// <summary>Downloads to staging, checks size and SHA256. Returns false with a reason set.</summary>
    private async Task<bool> DownloadToFileAsync(string url, string fileName, long expectedSize, string expectedSha,
        CancellationToken token, string what, Action<int, long, long> progress)
    {
        if (string.IsNullOrWhiteSpace(url)) { Fail(what + "地址为空"); return false; }
        var path = Path.Combine(StagingDir, Sanitize(fileName.Length > 0 ? fileName : "update.zip") + ".part");
        long received = 0;
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Fail($"{what}下载失败：HTTP {(int)resp.StatusCode}（可稍后重试，或到发布页手动下载）");
                return false;
            }
            var total = resp.Content.Headers.ContentLength ?? expectedSize;
            await using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var dst = File.Create(path);
            var buf = new byte[128 * 1024];
            int read;
            var lastPct = -1;
            while ((read = await src.ReadAsync(buf, token).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
                if (total > 0)
                {
                    var pct = (int)Math.Clamp(received * 100 / total, 0, 100);
                    if (pct != lastPct) { lastPct = pct; progress(pct, received, total); }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Fail($"{what}下载失败：{ex.Message}"); return false; }

        if (expectedSize > 0 && received != expectedSize)
        {
            TryDelete(path);
            Fail($"{what}不完整：收到 {Mb(received)} MB，应为 {Mb(expectedSize)} MB —— 已丢弃，当前版本不受影响");
            return false;
        }
        if (!string.IsNullOrEmpty(expectedSha))
        {
            var actual = await Task.Run(() => Sha256(path), token).ConfigureAwait(false);
            if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                Fail($"{what}校验失败（SHA256 不一致）：可能损坏或被篡改 —— 已丢弃，当前版本不受影响");
                return false;
            }
        }
        else if (expectedSize <= 0)
        {
            TryDelete(path);
            Fail($"{what}缺少大小与校验值，无法安全安装：请在发布页手动下载");
            return false;
        }
        return true;
    }

    private async Task<Manifest?> TryFetchManifestAsync(string url, CancellationToken token)
    {
        try
        {
            var json = await Http.GetStringAsync(url, token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var files = new List<ManifestFile>();
            if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    var p = f.TryGetProperty("path", out var pv) ? pv.GetString() ?? "" : "";
                    var s = f.TryGetProperty("size", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt64() : 0;
                    var h = f.TryGetProperty("sha256", out var hv) ? hv.GetString() ?? "" : "";
                    if (p.Length > 0) files.Add(new ManifestFile(p.Replace('\\', '/'), s, h));
                }
            }
            PatchInfo? patch = null;
            if (root.TryGetProperty("patch", out var pj) && pj.ValueKind == JsonValueKind.Object)
            {
                var name = pj.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "";
                var sz = pj.TryGetProperty("size", out var zv) && zv.ValueKind == JsonValueKind.Number ? zv.GetInt64() : 0;
                var sh = pj.TryGetProperty("sha256", out var qv) ? qv.GetString() ?? "" : "";
                var from = pj.TryGetProperty("from", out var fv) ? fv.GetString() ?? "" : "";
                // 变化文件清单随 patch 一起提供（客户端据此校验并判断是否覆盖全部差异）
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (pj.TryGetProperty("files", out var pf) && pf.ValueKind == JsonValueKind.Array)
                    foreach (var x in pf.EnumerateArray())
                    {
                        var xp = x.ValueKind == JsonValueKind.String ? x.GetString() ?? ""
                            : x.TryGetProperty("path", out var xv) ? xv.GetString() ?? "" : "";
                        if (xp.Length > 0) paths.Add(xp.Replace('\\', '/'));
                    }
                if (name.Length > 0 && paths.Count > 0)
                    patch = new PatchInfo(name, sz, sh, from, paths);
            }
            var version = root.TryGetProperty("version", out var vv) ? vv.GetString() ?? "" : "";
            return files.Count > 0 ? new Manifest(version, files, patch) : null;
        }
        catch { return null; }
    }

    private static List<ManifestFile> ComputeDiff(Manifest manifest)
    {
        var diff = new List<ManifestFile>();
        foreach (var f in manifest.Files)
        {
            var local = Path.Combine(AppContext.BaseDirectory, f.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(local)) { diff.Add(f); continue; }
                var len = new FileInfo(local).Length;
                if (f.Size > 0 && len != f.Size) { diff.Add(f); continue; }
                if (f.Sha256.Length == 0) continue;
                if (!string.Equals(Sha256(local), f.Sha256, StringComparison.OrdinalIgnoreCase)) diff.Add(f);
            }
            catch { diff.Add(f); }
        }
        return diff;
    }

    private (bool ok, string message) VerifyPayload(List<ManifestFile> expected)
    {
        foreach (var f in expected)
        {
            var local = Path.Combine(PayloadDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(local)) return (false, "增量包缺少文件 " + f.Path);
                if (f.Sha256.Length > 0 && !string.Equals(Sha256(local), f.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (false, "增量包文件校验失败：" + f.Path);
            }
            catch (Exception ex) { return (false, "增量包校验异常：" + ex.Message); }
        }
        return (true, "");
    }

    /// <summary>
    /// Version keys differ between sources: the app reports "0.1.1-maui" while the
    /// manifest/patch tags use "0.1.1". Compare on the normalized form.
    /// </summary>
    private static string Norm(string version)
    {
        var v = (version ?? "").Trim();
        if (v.EndsWith(AppIdentity.ChannelSuffix, StringComparison.OrdinalIgnoreCase))
            v = v[..^AppIdentity.ChannelSuffix.Length];
        return v.Trim();
    }

    /// <summary>Same release folder as the full package → the patch sits beside it.</summary>
    private static string ResolveSiblingUrl(string assetUrl, string fileName)
    {
        if (string.IsNullOrEmpty(assetUrl)) return "";
        var idx = assetUrl.LastIndexOf('/');
        return idx <= 0 ? "" : assetUrl[..(idx + 1)] + fileName;
    }

    /// <summary>Detached apply script: waits for our PID, backs up, copies, restarts.</summary>
    public bool ApplyAndRestart()
    {
        try
        {
            if (!File.Exists(ApplyScript)) return false;
            Set(new Status(Phase.Applying, 100, "正在重启并安装…", 0, 0, Current.StagedVersion, false, "", Current.Incremental));
            var pid = Environment.ProcessId;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{ApplyScript}\" {pid}\"",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Set(new Status(Phase.Failed, 0, "启动安装脚本失败：" + ex.Message, 0, 0, "", false, ""));
            return false;
        }
    }

    public Status ConsumeResult()
    {
        try
        {
            if (!File.Exists(ResultFile)) return Current;
            var text = File.ReadAllText(ResultFile).Trim();
            TryDelete(ResultFile);
            var ok = text.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
            Set(new Status(Phase.Idle, 0, ok ? "上次更新已完成 · " + text : "上次更新未完成 · " + text, 0, 0, "", true, text));
        }
        catch { }
        return Current;
    }

    public void Clear()
    {
        TryDelete(ResultFile);
        Set(new Status(Phase.Idle, 0, "", 0, 0, "", false, ""));
    }

    // ---------------- internals ----------------

    private void WriteApplyScript()
    {
        var script = """
@echo off
setlocal enableextensions
for %%a in ("%~dp0..") do set "APP=%%~fa"
set "PAY=%~dp0payload"
set "BAK=%~dp0..\update_backup\%date:~0,4%%date:~5,2%%date:~8,2%-%time:~0,2%%time:~3,2%%time:~6,2%"
set "PID=%~1"

for /l %%i in (1,1,90) do (
  tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul || goto :swapped
  ping -n 2 127.0.0.1 >nul
)

:swapped
robocopy "%APP%" "%BAK%" /E /XF config.json /XD data update_staging update_backup /NFL /NDL /NJH /NJS /R:1 /W:1 >nul 2>&1
robocopy "%PAY%" "%APP%" /E /XF config.json /XD data update_staging update_backup /NFL /NDL /NJH /NJS /R:2 /W:1 > "%~dp0apply.log" 2>&1
start "" "%APP%\BiLi_live_Tool.exe"
if %ERRORLEVEL% LEQ 7 (
  echo OK %date% %time% - files copied, app restarted> "%~dp0result.txt"
) else (
  echo FAIL %date% %time% - robocopy exited with %ERRORLEVEL%> "%~dp0result.txt"
)
endlocal
exit /b 0
""";
        File.WriteAllText(ApplyScript, script.Replace("\r\n", "\n").Replace("\n", "\r\n"), System.Text.Encoding.UTF8);
    }

    private static void ExtractZip(string zipPath, string targetDir, bool skipPatchJson)
    {
        Directory.CreateDirectory(targetDir);
        var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (skipPatchJson && name.Equals("patch.json", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.GetFullPath(Path.Combine(targetDir, name));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // zip-slip guard
            if (name.EndsWith("/"))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0");

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private Status Fail(string message)
    {
        Set(new Status(Phase.Failed, 0, message, 0, 0, "", false, ""));
        return Current;
    }

    private void Set(Status status)
    {
        lock (_lock) _status = status;
        try { Changed?.Invoke(); } catch { }
    }
}
