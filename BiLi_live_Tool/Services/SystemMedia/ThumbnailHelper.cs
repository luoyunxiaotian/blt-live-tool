#if WINDOWS
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace BiLi_live_Tool.Services.SystemMedia;

public static class ThumbnailHelper
{
    private const int MaxRetries = 8;
    private const int RetryDelayMs = 150;

    private static long _currentVersion = 0;
    private static string? _lastSavedHash = null;
    private static readonly object _hashLock = new();

    /// <summary>
    /// 异步拉取并校验封面，带 SHA256 比对与重试，避免读到切歌过渡期未刷新的上一首封面。
    /// </summary>
    public static void UpdateThumbnailAsync(
        Func<Task<IRandomAccessStreamReference?>> thumbnailProvider,
        Action<byte[]?, string?> onCoverReady)
    {
        if (thumbnailProvider == null) return;

        long myVersion = Interlocked.Increment(ref _currentVersion);

        Task.Run(async () =>
        {
            try
            {
                string? previousHash;
                lock (_hashLock)
                {
                    previousHash = _lastSavedHash;
                }

                byte[]? bestBytes = null;
                string? bestHash = null;

                for (int attempt = 0; attempt < MaxRetries; attempt++)
                {
                    if (Interlocked.Read(ref _currentVersion) != myVersion)
                    {
                        return; // 已被更新的切歌取代
                    }

                    var streamRef = await thumbnailProvider();
                    byte[]? bytes = await TryReadThumbnailBytesAsync(streamRef);
                    if (bytes != null && bytes.Length > 0)
                    {
                        string hash = ComputeHash(bytes);
                        bestBytes = bytes;
                        bestHash = hash;

                        // 封面内容与上一首不同，说明 SMTC 已经完成了封面更新
                        if (hash != previousHash)
                        {
                            break;
                        }
                    }

                    await Task.Delay(RetryDelayMs);
                }

                if (Interlocked.Read(ref _currentVersion) != myVersion)
                {
                    return;
                }

                if (bestBytes != null && bestBytes.Length > 0)
                {
                    lock (_hashLock)
                    {
                        _lastSavedHash = bestHash;
                    }
                    onCoverReady(bestBytes, bestHash);
                }
                else
                {
                    onCoverReady(null, null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ThumbnailHelper] Error: {ex.Message}");
                onCoverReady(null, null);
            }
        });
    }

    private static async Task<byte[]?> TryReadThumbnailBytesAsync(IRandomAccessStreamReference? streamRef)
    {
        if (streamRef == null) return null;
        try
        {
            using var stream = await streamRef.OpenReadAsync();
            if (stream == null || stream.Size == 0) return null;

            using var netStream = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeHash(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
}
#else
namespace BiLi_live_Tool.Services.SystemMedia;

public static class ThumbnailHelper { }
#endif
