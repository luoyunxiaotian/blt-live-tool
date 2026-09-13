// MossEngineInstaller.cs — on-demand installer for the MOSS local TTS engine.
//
// Package-size optimization: moss_tts_server.exe (≈44.5 MB) is no longer shipped
// inside the app bundle. It is fetched from the GitHub release assets on first use
// (the same channel the 728 MB model already uses) into <app>\tts — the exact
// directory TtsHost probes (search order: %BLT_TTS_DIR% → <app>\tts → dev Bin\bin)
// — so the next EnsureMoss() / RescanEngines() finds it with no extra wiring.
//
// The /releases/latest/download/<asset> shortcut resolves to the newest
// non-prerelease release and answers 404 until the repository publishes one, so
// every failure mode degrades to an actionable manual-placement hint instead of
// an exception.
//
// Static, no DI registration: MauiProgram is out of this change's scope. A
// SemaphoreSlim keeps concurrent callers single-flight.

using System.Globalization;
using System.Net.Http.Headers;

namespace BiLi_live_Tool.Services;

/// <summary>Installer snapshot for the panel (serializes camelCase, like the other API payloads).</summary>
/// <param name="Installed">A usable moss_tts_server.exe exists on one of the TtsHost search paths.</param>
/// <param name="Installing">A download is in flight — the UI must keep its button disabled.</param>
/// <param name="Progress">0-100 when Content-Length is known; 0 also means unknown / not started.</param>
/// <param name="Message">Chinese status or actionable hint (empty before the first run).</param>
/// <param name="TargetPath">Where the exe must end up for the app to find it.</param>
/// <param name="SourceUrl">GitHub "latest release" asset URL used for the download.</param>
public sealed record MossEngineStatus(
    bool Installed,
    bool Installing,
    int Progress,
    string Message,
    string TargetPath,
    string SourceUrl);

/// <summary>
/// Single-flight downloader for <c>moss_tts_server.exe</c>. Public surface:
/// <see cref="Status"/> (UI snapshot), <see cref="EnsureInstalledAsync"/> (idempotent
/// install) and the <see cref="Changed"/> event (progress repaint). Never throws for
/// network/IO problems — the failure text lands in <see cref="MossEngineStatus.Message"/>.
/// </summary>
public static class MossEngineInstaller
{
    /// <summary>Release asset name — the same file name TtsHost searches for.</summary>
    public const string ExeName = "moss_tts_server.exe";

    /// <summary>The real asset is ≈44.5 MB; anything smaller is an error page, not the exe.</summary>
    private const long MinValidBytes = 1024 * 1024;

    private const int BufferSize = 128 * 1024;

    /// <summary>Single-flight gate: a concurrent caller waits instead of downloading twice.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly object Sync = new();

    // 44.5 MB needs more than the default 100 s on slow links; GitHub is not rate limited here.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue(AppIdentity.CodeName, "1.0") } },
    };

    private static bool _installing;
    private static int _progress;
    private static string _message = "";

    /// <summary>Raised after every visible state/progress change (may be a background thread).</summary>
    public static event Action? Changed;

    /// <summary>Engine directory probed by TtsHost right after %BLT_TTS_DIR%.</summary>
    public static string TargetDir => Path.Combine(AppContext.BaseDirectory, "tts");

    /// <summary>Final exe location; the download first lands in <see cref="TargetPath"/> + ".part".</summary>
    public static string TargetPath => Path.Combine(TargetDir, ExeName);

    /// <summary>GitHub "latest release" asset URL (404s until the first release exists).</summary>
    public static string SourceUrl => AppIdentity.RepoUrl + "/releases/latest/download/" + ExeName;

    /// <summary>Live probe (no caching) — true when the engine exe is already available.</summary>
    public static bool IsInstalled => TtsHost.FindMossExe() != null;

    /// <summary>Current snapshot for the UI; does a cheap File.Exists probe for <c>Installed</c>.</summary>
    public static MossEngineStatus Status()
    {
        bool installing;
        int progress;
        string message;
        lock (Sync)
        {
            installing = _installing;
            progress = _progress;
            message = _message;
        }
        return new MossEngineStatus(TtsHost.FindMossExe() != null, installing, progress, message, TargetPath, SourceUrl);
    }

    /// <summary>
    /// Ensures a usable engine exe, downloading it when needed. Returns true when the
    /// exe is in place afterwards; false when it could not be obtained — the reason is
    /// then in <see cref="MossEngineStatus.Message"/>. Network/IO failures and a cancel
    /// during the download are reported as messages; only a cancel while waiting for
    /// the single-flight gate propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<bool> EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (IsInstalled) return true;                     // fast path: nothing to do

        await Gate.WaitAsync(ct).ConfigureAwait(false);   // one download at a time
        try
        {
            if (IsInstalled) return true;                 // finished while we waited
            return await DownloadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> DownloadAsync(CancellationToken ct)
    {
        var target = TargetPath;
        var part = target + ".part";

        SetState(true, 0, "正在连接下载源…");
        try
        {
            Directory.CreateDirectory(TargetDir);
        }
        catch (Exception ex)
        {
            SetState(false, 0, "无法创建目录（" + Short(ex.Message) + "）：请手动把 " + ExeName + " 放到 " + target);
            return false;
        }

        try
        {
            using var resp = await Http.GetAsync(SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                SetState(false, 0, HttpFailText((int)resp.StatusCode, target));
                return false;
            }

            var total = resp.Content.Headers.ContentLength ?? -1;
            long done = 0;
            var lastPercent = -1;
            long lastMb = -1;

            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

            var buffer = new byte[BufferSize];
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;

                if (total > 0)
                {
                    var percent = (int)(done * 100 / total);
                    if (percent > 100) percent = 100;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        SetState(true, percent, Mb(done) + " / " + Mb(total));
                    }
                }
                else
                {
                    // No Content-Length (chunked/proxy): show an indeterminate byte count.
                    var mb = done / (1024 * 1024);
                    if (mb != lastMb)
                    {
                        lastMb = mb;
                        SetState(true, 0, "已接收 " + mb + " MB（总大小未知）");
                    }
                }
            }

            if (done < MinValidBytes)
            {
                TryDelete(part);
                SetState(false, 0, "下载内容异常（仅 " + Mb(done) + "，不是引擎程序）：请重试，或手动把 " + ExeName + " 放到 " + target);
                return false;
            }

            try
            {
                // Rename only after the whole payload is on disk → never a half engine.
                File.Move(part, target, overwrite: true);
            }
            catch (Exception ex)
            {
                TryDelete(part);
                SetState(false, 0, "写入失败（" + Short(ex.Message) + "）：请先停止 MOSS 服务后重试，或手动把 " + ExeName + " 放到 " + target);
                return false;
            }

            SetState(false, 0, "已安装：" + target);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            SetState(false, 0, "下载已取消：请重试，或手动把 " + ExeName + " 放到 " + target);
            return false;
        }
        catch (Exception ex)
        {
            TryDelete(part);
            SetState(false, 0, "下载失败（" + Short(ex.Message) + "）：请检查网络/代理后重试，或手动把 " + ExeName + " 放到 " + target);
            return false;
        }
    }

    /// <summary>403/404 means "repository or release not published yet" — the expected pre-release state.</summary>
    private static string HttpFailText(int code, string target)
        => code is 403 or 404
            ? "更新源暂无该文件（仓库/Release 未发布）：请手动把 " + ExeName + " 放到 " + target
            : "下载失败（HTTP " + code + "）：请稍后重试，或手动把 " + ExeName + " 放到 " + target;

    private static void SetState(bool installing, int progress, string message)
    {
        lock (Sync)
        {
            _installing = installing;
            _progress = progress;
            _message = message;
        }
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // A UI subscriber must never break the download.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best effort */ }
    }

    private static string Mb(long bytes)
        => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "未知错误";
        return s.Length > 90 ? s[..90] + "…" : s;
    }
}
