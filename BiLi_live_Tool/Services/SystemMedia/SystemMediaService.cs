#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BiLi_live_Tool.Services;
using CSCore.CoreAudioAPI;
using Windows.Media.Control;

namespace BiLi_live_Tool.Services.SystemMedia;

public class SystemMediaService : IDisposable
{
    private readonly object _lock = new();
    private GlobalSystemMediaTransportControlsSessionManager? _smtcManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private AudioSessionManager2? _audioSessionManager;
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMNotificationClient? _notificationClient;
    private Timer? _pollTimer;

    private readonly EventHub _hub;
    private readonly SongPlayer _songPlayer;
    private readonly AppConfig _config;
    private static readonly HttpClient _httpClient = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(6) };
    private readonly ConcurrentDictionary<string, byte[]> _coverCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled = true;
    private bool _ignoreBrowsers = true;
    private bool _preferInternalPlayer = true;
    private string _prevSignature = "";
    private string _prevStatus = "";
    private string _lastInternalCoverUrl = "";
    private string? _currentCoverSig;
    private byte[]? _currentCoverBytes;
    private string? _currentCoverHash;
    private readonly ConcurrentDictionary<string, double> _durationCache = new(StringComparer.OrdinalIgnoreCase);
    private double _trackedPositionSec = 0;
    private long _lastNeteasePlaytimeMs = 0;
    private double _prevReportedPosition = -1;
    private double _prevReportedDuration = -1;

    public SystemMediaService(EventHub hub, SongPlayer songPlayer, AppConfig config)
    {
        _hub = hub;
        _songPlayer = songPlayer;
        _config = config;
        _preferInternalPlayer = _config.PreferInternalPlayer;
        _ignoreBrowsers = _config.IgnoreBrowsers;

        _songPlayer.Changed += () =>
        {
            _ = UpdateMediaStateAsync();
        };

        TrackChanged += t => _hub.PublishOutbound("system_media", t);
        StatusChanged += s => _hub.PublishOutbound("system_media", CurrentTrack);
        ProgressChanged += (pos, dur) => _hub.PublishOutbound("system_media_progress", new { position = pos, duration = dur });
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                if (!value)
                {
                    ResetTrack();
                }
            }
        }
    }

    public bool IgnoreBrowsers
    {
        get => _ignoreBrowsers;
        set
        {
            if (_ignoreBrowsers != value)
            {
                _ignoreBrowsers = value;
                _config.IgnoreBrowsers = value;
                _ = UpdateMediaStateAsync();
            }
        }
    }

    public bool PreferInternalPlayer
    {
        get => _preferInternalPlayer;
        set
        {
            if (_preferInternalPlayer != value)
            {
                _preferInternalPlayer = value;
                _config.PreferInternalPlayer = value;
                _ = UpdateMediaStateAsync();
            }
        }
    }

    public SystemMediaTrack CurrentTrack { get; private set; } = new();

    public byte[]? CurrentCoverBytes
    {
        get
        {
            lock (_lock) return _currentCoverBytes;
        }
    }

    public string? CurrentCoverHash
    {
        get
        {
            lock (_lock) return _currentCoverHash;
        }
    }

    public event Action<SystemMediaTrack>? TrackChanged;
    public event Action<string>? StatusChanged;
    public event Action<double, double>? ProgressChanged;

    public void SimulateTrack(SystemMediaTrack track)
    {
        CurrentTrack = track;
        TrackChanged?.Invoke(track);
    }

    public void Start()
    {
        Task.Run(async () =>
        {
            try
            {
                await InitSmtcAsync();
                InitWasapi();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemMediaService] Init error: {ex.Message}");
            }

            _pollTimer = new Timer(OnPollTick, null, 1000, 1000);
        });
    }

    private async Task InitSmtcAsync()
    {
        try
        {
            _smtcManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_smtcManager != null)
            {
                _smtcManager.CurrentSessionChanged += OnSmtcCurrentSessionChanged;
                _smtcManager.SessionsChanged += OnSmtcSessionsChanged;
                AttachSession(_smtcManager.GetCurrentSession());
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemMediaService] SMTC Init failed: {ex.Message}");
        }
    }

    private void InitWasapi()
    {
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            using var defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice != null)
            {
                _audioSessionManager = AudioSessionManager2.FromMMDevice(defaultDevice);
            }

            _notificationClient = new MMNotificationClient(_deviceEnumerator);
            _notificationClient.DefaultDeviceChanged += (s, e) =>
            {
                lock (_lock)
                {
                    try
                    {
                        using var newDev = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        if (newDev != null)
                        {
                            _audioSessionManager = AudioSessionManager2.FromMMDevice(newDev);
                        }
                    }
                    catch { }
                }
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemMediaService] WASAPI Init failed: {ex.Message}");
        }
    }

    private void OnSmtcCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        AttachSession(sender.GetCurrentSession());
        _ = UpdateMediaStateAsync();
    }

    private void OnSmtcSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        if (_currentSession == null)
        {
            AttachSession(sender.GetCurrentSession());
            _ = UpdateMediaStateAsync();
        }
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        lock (_lock)
        {
            if (_currentSession != null)
            {
                try
                {
                    _currentSession.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
                    _currentSession.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
                    _currentSession.TimelinePropertiesChanged -= OnSessionTimelinePropertiesChanged;
                }
                catch { }
            }

            _currentSession = session;

            if (_currentSession != null)
            {
                try
                {
                    _currentSession.PlaybackInfoChanged += OnSessionPlaybackInfoChanged;
                    _currentSession.MediaPropertiesChanged += OnSessionMediaPropertiesChanged;
                    _currentSession.TimelinePropertiesChanged += OnSessionTimelinePropertiesChanged;
                }
                catch { }
            }
        }
    }

    private void OnSessionPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        _ = UpdateMediaStateAsync();
    }

    private void OnSessionMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = UpdateMediaStateAsync();
    }

    private void OnSessionTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        try
        {
            var timeline = sender.GetTimelineProperties();
            double pos = timeline.Position.TotalSeconds;
            double dur = timeline.EndTime.TotalSeconds;
            if (dur > 0 && Math.Abs(pos - CurrentTrack.PositionSec) > 0.5)
            {
                CurrentTrack.PositionSec = pos;
                CurrentTrack.DurationSec = dur;
                ProgressChanged?.Invoke(pos, dur);
            }
        }
        catch { }
    }

    private void OnPollTick(object? state)
    {
        if (!_enabled) return;
        _ = UpdateMediaStateAsync();
    }

    public async Task UpdateMediaStateAsync()
    {
        if (!_enabled) return;

        try
        {
            string title = "";
            string artist = "";
            string album = "";
            string status = "None";
            string sourceApp = "";
            string requester = "";
            string platform = "";
            string songId = "";
            double position = 0;
            double duration = 0;
            float volumePeak = 0;
            bool fromSmtc = false;
            bool fromInternal = false;

            // 1. 优先本程序点歌（内部 SongPlayer）
            if (_preferInternalPlayer && (_songPlayer.IsPlaying || (!string.IsNullOrWhiteSpace(_songPlayer.CurrentName) && _songPlayer.PlayingIndex >= 0)))
            {
                title = _songPlayer.CurrentName;
                artist = _songPlayer.CurrentArtist;
                album = _songPlayer.CurrentAlbum;
                status = _songPlayer.IsPlaying ? "Playing" : "Paused";
                sourceApp = "直播小帮手点歌" + (string.IsNullOrEmpty(_songPlayer.CurrentPlatform) ? "" : $" [{_songPlayer.CurrentPlatform}]");
                position = _songPlayer.Current;
                duration = _songPlayer.Duration;
                requester = _songPlayer.CurrentRequester;
                platform = _songPlayer.CurrentPlatform;
                songId = _songPlayer.CurrentSongId;
                fromInternal = true;

                // 封面抓取/同步
                var coverUrl = _songPlayer.CurrentCoverUrl;
                if (!string.IsNullOrWhiteSpace(coverUrl))
                {
                    if (coverUrl != _lastInternalCoverUrl)
                    {
                        _lastInternalCoverUrl = coverUrl;
                        _ = DownloadInternalCoverAsync(coverUrl);
                    }
                }
                else
                {
                    _lastInternalCoverUrl = "";
                    lock (_lock)
                    {
                        _currentCoverBytes = null;
                        _currentCoverHash = null;
                    }
                }
            }

            // 2. 若内部没有在播放，则检测外部 Windows SMTC 会话
            if (!fromInternal)
            {
                GlobalSystemMediaTransportControlsSession? session = null;
                GlobalSystemMediaTransportControlsSessionMediaProperties? mediaProps = null;
                try
                {
                    var sessions = _smtcManager?.GetSessions();
                    if (sessions != null && sessions.Count > 0)
                    {
                        session = sessions
                            .OrderByDescending(s => ScoreSession(s, _ignoreBrowsers))
                            .FirstOrDefault();

                        if (session != null && ScoreSession(session, _ignoreBrowsers) < 0)
                        {
                            session = null;
                        }
                    }
                }
                catch { }

                if (session == null && !_ignoreBrowsers)
                {
                    lock (_lock)
                    {
                        session = _currentSession ?? _smtcManager?.GetCurrentSession();
                    }
                }

                if (session != null)
                {
                    try
                    {
                        sourceApp = CleanAppId(session.SourceAppUserModelId);
                        var playbackInfo = session.GetPlaybackInfo();
                        if (playbackInfo != null)
                        {
                            status = playbackInfo.PlaybackStatus switch
                            {
                                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Playing",
                                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Paused",
                                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "None",
                                _ => "None"
                            };
                        }

                        mediaProps = await session.TryGetMediaPropertiesAsync();
                        if (mediaProps != null && (!string.IsNullOrWhiteSpace(mediaProps.Title) || !string.IsNullOrWhiteSpace(mediaProps.Artist)))
                        {
                            title = mediaProps.Title?.Trim() ?? "";
                            artist = mediaProps.Artist?.Trim() ?? "";
                            album = mediaProps.AlbumTitle?.Trim() ?? "";
                            fromSmtc = true;
                        }

                        var timeline = session.GetTimelineProperties();
                        if (timeline != null)
                        {
                            position = timeline.Position.TotalSeconds;
                            duration = timeline.EndTime.TotalSeconds;
                        }
                    }
                    catch { }
                }

                // WASAPI 音量采样辅助检测
                volumePeak = SampleProcessVolume(new[] { "cloudmusic", "QQMusic", "kugou", "Spotify", "potplayer", "foobar2000" });

                // 尝试优先读取网易云音乐本地 SQLite 数据库（webdb.dat）
                bool isNeteaseCandidate = (!fromSmtc || string.IsNullOrWhiteSpace(title) || sourceApp.Contains("网易") || sourceApp.Contains("cloudmusic"));
                if (isNeteaseCandidate)
                {
                    var neteaseDb = NeteaseWebDbReader.TryGetLatestTrack();
                    if (neteaseDb != null && !string.IsNullOrWhiteSpace(neteaseDb.Title))
                    {
                        platform = "netease";
                        if (!string.IsNullOrEmpty(neteaseDb.SongId)) songId = neteaseDb.SongId;

                        if (!fromSmtc || string.IsNullOrWhiteSpace(title))
                        {
                            title = neteaseDb.Title;
                            artist = neteaseDb.Artist;
                            album = neteaseDb.Album;
                            sourceApp = "网易云音乐";
                            status = volumePeak > 0.0001f ? "Playing" : "Paused";
                        }

                        if (duration <= 0 && neteaseDb.DurationSec > 0)
                        {
                            duration = neteaseDb.DurationSec;
                        }

                        if (neteaseDb.PlaytimeMs > 0 && neteaseDb.PlaytimeMs != _lastNeteasePlaytimeMs)
                        {
                            _lastNeteasePlaytimeMs = neteaseDb.PlaytimeMs;
                            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            var elapsed = (nowMs - neteaseDb.PlaytimeMs) / 1000.0;
                            if (elapsed >= 0 && (duration <= 0 || elapsed <= duration + 2))
                            {
                                _trackedPositionSec = Math.Min(elapsed, duration > 0 ? duration : elapsed);
                            }
                            else
                            {
                                _trackedPositionSec = 0;
                            }
                        }

                        // 如果网易云数据库提供了封面且当前无封面
                        if (!string.IsNullOrEmpty(neteaseDb.CoverUrl) && _currentCoverBytes == null)
                        {
                            _ = DownloadAndApplyCoverAsync(neteaseDb.CoverUrl, $"{title} - {artist}", album);
                        }
                    }
                }

                // 如果 SMTC 没有检出有效歌曲，尝试探测本地音乐软件窗口标题
                if (!fromSmtc || string.IsNullOrWhiteSpace(title))
                {
                    var fallback = TryDetectLocalMusicWindow();
                    if (fallback != null)
                    {
                        title = fallback.Value.Title;
                        artist = fallback.Value.Artist;
                        sourceApp = fallback.Value.AppName;
                        status = volumePeak > 0.0001f ? "Playing" : "Paused";
                    }
                }
                else if (status == "None" && volumePeak > 0.0001f)
                {
                    // 音量在出声，但 SMTC 显示 None/Paused，校正为 Playing
                    status = "Playing";
                }

                if (string.IsNullOrEmpty(platform) && !string.IsNullOrWhiteSpace(title))
                {
                    var lowerApp = (sourceApp ?? "").ToLowerInvariant();
                    if (lowerApp.Contains("qq") || lowerApp.Contains("tencent")) platform = "qq";
                    else if (lowerApp.Contains("kugou") || lowerApp.Contains("酷狗")) platform = "kugou";
                    else if (lowerApp.Contains("kuwo") || lowerApp.Contains("酷我")) platform = "kuwo";
                    else if (lowerApp.Contains("163") || lowerApp.Contains("netease") || lowerApp.Contains("网易云")) platform = "netease";
                }

                // 尝试从缓存中恢复 duration
                string sig = $"{title} - {artist}";
                if (duration <= 0 && !string.IsNullOrWhiteSpace(title) && _durationCache.TryGetValue(sig, out var cachedDur) && cachedDur > 0)
                {
                    duration = cachedDur;
                }

                // 进度追踪与自推进（非内部点歌时）
                if (!fromInternal && !string.IsNullOrWhiteSpace(title))
                {
                    if (sig != _prevSignature)
                    {
                        if (_lastNeteasePlaytimeMs <= 0)
                        {
                            _trackedPositionSec = 0;
                        }
                    }
                    else
                    {
                        if (position > 0)
                        {
                            _trackedPositionSec = position;
                        }
                        else
                        {
                            if (status == "Playing")
                            {
                                _trackedPositionSec += 1.0;
                                if (duration > 0 && _trackedPositionSec > duration)
                                {
                                    _trackedPositionSec = duration;
                                }
                            }
                            position = _trackedPositionSec;
                        }
                    }
                }

                if (duration > 0 && !string.IsNullOrWhiteSpace(title))
                {
                    _durationCache[sig] = duration;
                }

                // 外部媒体封面抓取（SMTC 原生流优先，失败或无封面时自动在线曲库搜图）
                bool hasExternalSong = !string.IsNullOrWhiteSpace(title);
                if (hasExternalSong)
                {
                    if (sig != _currentCoverSig || _currentCoverBytes == null)
                    {
                        _currentCoverSig = sig;

                        // 1. 尝试从本地内存缓存读取
                        if (_coverCache.TryGetValue(sig, out var cachedBytes) && cachedBytes != null && cachedBytes.Length > 0)
                        {
                            lock (_lock)
                            {
                                _currentCoverBytes = cachedBytes;
                                _currentCoverHash = ComputeHash(cachedBytes);
                            }
                        }
                        else
                        {
                            // 2. 如果 SMTC 提供了封面流，先尝试从 SMTC 读取
                            if (session != null && mediaProps?.Thumbnail != null)
                            {
                                ThumbnailHelper.UpdateThumbnailAsync(
                                    async () =>
                                    {
                                        try
                                        {
                                            var p = await session.TryGetMediaPropertiesAsync();
                                            return p?.Thumbnail;
                                        }
                                        catch { return null; }
                                    },
                                    (bytes, hash) =>
                                    {
                                        if (bytes != null && bytes.Length > 0)
                                        {
                                            _coverCache[sig] = bytes;
                                            lock (_lock)
                                            {
                                                if (_currentCoverSig == sig)
                                                {
                                                    _currentCoverBytes = bytes;
                                                    _currentCoverHash = hash;
                                                    CurrentTrack.CoverHash = hash ?? "";
                                                    CurrentTrack.CoverUrl = "/api/media/cover";
                                                }
                                            }
                                            TrackChanged?.Invoke(CurrentTrack);
                                        }
                                        else
                                        {
                                            // SMTC 没读到有效封面，走在线曲库搜图兜底
                                            _ = FetchOnlineCoverFallbackAsync(title, artist, sourceApp, sig);
                                        }
                                    });
                            }
                            else
                            {
                                // 3. SMTC 无封面或通过窗口探测捕获（例如网易云），直接走在线歌曲封面搜索
                                _ = FetchOnlineCoverFallbackAsync(title, artist, sourceApp, sig);
                            }
                        }
                    }
                }
            }

            bool hasSong = !string.IsNullOrWhiteSpace(title);
            if (!hasSong)
            {
                status = "None";
                lock (_lock)
                {
                    _currentCoverBytes = null;
                    _currentCoverHash = null;
                    _currentCoverSig = null;
                    _trackedPositionSec = 0;
                    _lastNeteasePlaytimeMs = 0;
                    _prevReportedPosition = -1;
                    _prevReportedDuration = -1;
                }
            }

            var updated = new SystemMediaTrack
            {
                HasSong = hasSong,
                Status = status,
                Title = title,
                Artist = artist,
                Album = album,
                PositionSec = position,
                DurationSec = duration,
                CoverHash = _currentCoverHash ?? "",
                CoverUrl = _currentCoverBytes != null ? "/api/media/cover" : "",
                SourceApp = sourceApp,
                Requester = requester,
                Platform = platform,
                SongId = songId,
                VolumePeak = volumePeak,
                UpdatedAt = DateTime.UtcNow
            };

            bool trackChanged = !string.Equals($"{title} - {artist}", _prevSignature, StringComparison.Ordinal);
            bool statusChanged = !string.Equals(status, _prevStatus, StringComparison.Ordinal);

            CurrentTrack = updated;
            _prevSignature = $"{title} - {artist}";
            _prevStatus = status;

            if (trackChanged)
            {
                TrackChanged?.Invoke(updated);
            }
            if (statusChanged)
            {
                StatusChanged?.Invoke(status);
            }
            if (hasSong && (Math.Abs(position - _prevReportedPosition) >= 0.5 || Math.Abs(duration - _prevReportedDuration) >= 0.5))
            {
                _prevReportedPosition = position;
                _prevReportedDuration = duration;
                ProgressChanged?.Invoke(position, duration);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemMediaService] UpdateMediaState error: {ex.Message}");
        }
    }

    private async Task DownloadInternalCoverAsync(string url)
    {
        try
        {
            if (_coverCache.TryGetValue(url, out var cached) && cached != null && cached.Length > 0)
            {
                var h = ComputeHash(cached);
                lock (_lock)
                {
                    _currentCoverBytes = cached;
                    _currentCoverHash = h;
                    CurrentTrack.CoverHash = h;
                    CurrentTrack.CoverUrl = "/api/media/cover";
                }
                TrackChanged?.Invoke(CurrentTrack);
                return;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            if (url.Contains("bilibili") || url.Contains("hdslb"))
                req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            else if (url.Contains("qq.com") || url.Contains("gtimg.cn"))
                req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
            else if (url.Contains("163.com") || url.Contains("126.net"))
                req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");

            using var resp = await _httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 0)
                {
                    var hash = ComputeHash(bytes);
                    _coverCache[url] = bytes;
                    lock (_lock)
                    {
                        _currentCoverBytes = bytes;
                        _currentCoverHash = hash;
                        CurrentTrack.CoverHash = hash;
                        CurrentTrack.CoverUrl = "/api/media/cover";
                    }
                    TrackChanged?.Invoke(CurrentTrack);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemMediaService] Download cover error: {ex.Message}");
        }
    }

    private async Task FetchOnlineCoverFallbackAsync(string title, string artist, string sourceApp, string sig)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(title) || title == "未知曲目") return;

            string query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            string? coverUrl = null;
            string? resolvedAlbum = null;

            var srcLower = (sourceApp ?? "").ToLowerInvariant();
            bool isQqFirst = srcLower.Contains("qq") || srcLower.Contains("tencent");
            bool isKugouFirst = srcLower.Contains("kugou") || srcLower.Contains("酷狗");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));

            double resolvedDuration = 0;

            // 网易云音乐搜索（曲库全、封面高清）
            if (!isQqFirst && !isKugouFirst)
            {
                try
                {
                    var neteaseHits = await MusicApi.NeteaseSearchAsync(query, 3, "song", cts.Token);
                    var hit = neteaseHits.FirstOrDefault(s => !string.IsNullOrEmpty(s.Cover));
                    if (hit != null)
                    {
                        coverUrl = hit.Cover;
                        resolvedAlbum = hit.Album;
                        if (!string.IsNullOrEmpty(hit.Duration) && double.TryParse(hit.Duration, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dur) && dur > 0)
                        {
                            resolvedDuration = dur;
                        }
                    }
                }
                catch { }
            }

            // QQ音乐搜索
            if (string.IsNullOrEmpty(coverUrl) && !isKugouFirst)
            {
                try
                {
                    var qqHits = await MusicApi.QqSearchAsync(query, 3, "song", cts.Token);
                    var hit = qqHits.FirstOrDefault(s => !string.IsNullOrEmpty(s.Cover));
                    if (hit != null)
                    {
                        coverUrl = hit.Cover;
                        if (string.IsNullOrEmpty(resolvedAlbum)) resolvedAlbum = hit.Album;
                        if (resolvedDuration <= 0 && !string.IsNullOrEmpty(hit.Duration) && double.TryParse(hit.Duration, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dur) && dur > 0)
                        {
                            resolvedDuration = dur;
                        }
                    }
                }
                catch { }
            }

            // 酷狗音乐搜索
            if (string.IsNullOrEmpty(coverUrl))
            {
                try
                {
                    var kgHits = await MusicApi.KugouSearchAsync(query, 3, cts.Token);
                    var hit = kgHits.FirstOrDefault(s => !string.IsNullOrEmpty(s.Cover));
                    if (hit != null)
                    {
                        coverUrl = hit.Cover;
                        if (string.IsNullOrEmpty(resolvedAlbum)) resolvedAlbum = hit.Album;
                        if (resolvedDuration <= 0 && !string.IsNullOrEmpty(hit.Duration) && double.TryParse(hit.Duration, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dur) && dur > 0)
                        {
                            resolvedDuration = dur;
                        }
                    }
                }
                catch { }
            }

            // 兜底再次检查网易云
            if (string.IsNullOrEmpty(coverUrl) && (isQqFirst || isKugouFirst))
            {
                try
                {
                    var neteaseHits = await MusicApi.NeteaseSearchAsync(query, 3, "song", cts.Token);
                    var hit = neteaseHits.FirstOrDefault(s => !string.IsNullOrEmpty(s.Cover));
                    if (hit != null)
                    {
                        coverUrl = hit.Cover;
                        if (string.IsNullOrEmpty(resolvedAlbum)) resolvedAlbum = hit.Album;
                        if (resolvedDuration <= 0 && !string.IsNullOrEmpty(hit.Duration) && double.TryParse(hit.Duration, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dur) && dur > 0)
                        {
                            resolvedDuration = dur;
                        }
                    }
                }
                catch { }
            }

            if (resolvedDuration > 0)
            {
                _durationCache[sig] = resolvedDuration;
                if (CurrentTrack.DurationSec <= 0 && _currentCoverSig == sig)
                {
                    CurrentTrack.DurationSec = resolvedDuration;
                    ProgressChanged?.Invoke(CurrentTrack.PositionSec, resolvedDuration);
                }
            }

            if (string.IsNullOrWhiteSpace(coverUrl)) return;
            if (_currentCoverSig != sig) return;

            await DownloadAndApplyCoverAsync(coverUrl, sig, resolvedAlbum);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemMediaService] Online cover fetch error: {ex.Message}");
        }
    }

    private async Task DownloadAndApplyCoverAsync(string url, string sig, string? resolvedAlbum)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            if (url.Contains("bilibili") || url.Contains("hdslb"))
                req.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            else if (url.Contains("qq.com") || url.Contains("gtimg.cn"))
                req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
            else if (url.Contains("163.com") || url.Contains("126.net"))
                req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");

            using var resp = await _httpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 0)
                {
                    var hash = ComputeHash(bytes);
                    _coverCache[sig] = bytes;

                    lock (_lock)
                    {
                        if (_currentCoverSig == sig)
                        {
                            _currentCoverBytes = bytes;
                            _currentCoverHash = hash;
                            CurrentTrack.CoverHash = hash;
                            CurrentTrack.CoverUrl = "/api/media/cover";
                            if (!string.IsNullOrEmpty(resolvedAlbum) && string.IsNullOrEmpty(CurrentTrack.Album))
                            {
                                CurrentTrack.Album = resolvedAlbum;
                            }
                        }
                    }
                    TrackChanged?.Invoke(CurrentTrack);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemMediaService] Download online cover error: {ex.Message}");
        }
    }

    private static string ComputeHash(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(bytes));
    }

    private static int ScoreSession(GlobalSystemMediaTransportControlsSession s, bool ignoreBrowsers)
    {
        if (s == null) return 0;
        string appId = s.SourceAppUserModelId ?? "";
        bool isBrowser = IsBrowserApp(appId);
        if (ignoreBrowsers && isBrowser) return -1;

        bool isPlaying = false;
        try
        {
            var pInfo = s.GetPlaybackInfo();
            isPlaying = pInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { }

        bool isMusic = IsMusicApp(appId);

        if (isMusic && isPlaying) return 100;
        if (!isBrowser && isPlaying) return 80;
        if (isMusic) return 60;
        if (!isBrowser) return 40;
        if (!ignoreBrowsers && isPlaying) return 20;
        if (!ignoreBrowsers) return 10;
        return 0;
    }

    private static bool IsBrowserApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        var lower = appId.ToLowerInvariant();
        return lower.Contains("msedge") || lower.Contains("edge") ||
               lower.Contains("chrome") || lower.Contains("firefox") ||
               lower.Contains("opera") || lower.Contains("brave") ||
               lower.Contains("whale") || lower.Contains("vivaldi") ||
               lower.Contains("360se") || lower.Contains("se.exe") ||
               lower.Contains("sogouexplorer");
    }

    private static bool IsMusicApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        var lower = appId.ToLowerInvariant();
        return lower.Contains("cloudmusic") || lower.Contains("qqmusic") ||
               lower.Contains("kugou") || lower.Contains("kuwo") ||
               lower.Contains("spotify") || lower.Contains("applemusic") ||
               lower.Contains("soda") || lower.Contains("cider") ||
               lower.Contains("foobar") || lower.Contains("aimp") ||
               lower.Contains("yesplaymusic") || lower.Contains("music");
    }

    private (string Title, string Artist, string AppName)? TryDetectLocalMusicWindow()
    {
        // 1. 网易云音乐 (cloudmusic)
        var neteaseTitles = WindowDetector.GetWindowTitles("cloudmusic");
        foreach (var raw in neteaseTitles)
        {
            var t = raw.Trim();
            if (t.EndsWith(" - 网易云音乐", StringComparison.OrdinalIgnoreCase))
                t = t[..^" - 网易云音乐".Length].Trim();
            if (t.StartsWith("网易云音乐 - ", StringComparison.OrdinalIgnoreCase))
                t = t["网易云音乐 - ".Length..].Trim();

            if (t.Contains(" - ") && !t.Contains("MediaPlayer") && t != "网易云音乐")
            {
                var idx = t.LastIndexOf(" - ", StringComparison.Ordinal);
                if (idx > 0)
                {
                    return (t[..idx].Trim(), t[(idx + 3)..].Trim(), "网易云音乐");
                }
            }
        }

        // 2. QQ音乐 (QQMusic)
        var qqTitles = WindowDetector.GetWindowTitles("QQMusic");
        foreach (var raw in qqTitles)
        {
            var t = raw.Trim();
            if (t.EndsWith(" - QQ音乐", StringComparison.OrdinalIgnoreCase))
                t = t[..^" - QQ音乐".Length].Trim();
            if (t.StartsWith("QQ音乐 - ", StringComparison.OrdinalIgnoreCase))
                t = t["QQ音乐 - ".Length..].Trim();

            if (t.Contains(" - ") && t != "QQ音乐")
            {
                var idx = t.LastIndexOf(" - ", StringComparison.Ordinal);
                if (idx > 0)
                {
                    return (t[..idx].Trim(), t[(idx + 3)..].Trim(), "QQ音乐");
                }
            }
        }

        // 3. 酷狗音乐 (KuGou)
        var kgTitles = WindowDetector.GetWindowTitles("KuGou");
        foreach (var raw in kgTitles)
        {
            var t = raw.Trim();
            if (t.EndsWith(" - 酷狗音乐", StringComparison.OrdinalIgnoreCase))
                t = t[..^" - 酷狗音乐".Length].Trim();
            if (t.StartsWith("酷狗音乐 - ", StringComparison.OrdinalIgnoreCase))
                t = t["酷狗音乐 - ".Length..].Trim();

            if (t.Contains(" - ") && t != "酷狗音乐")
            {
                var idx = t.LastIndexOf(" - ", StringComparison.Ordinal);
                if (idx > 0)
                {
                    return (t[..idx].Trim(), t[(idx + 3)..].Trim(), "酷狗音乐");
                }
            }
        }

        // 4. Foobar2000
        var foobarTitle = WindowDetector.GetWindowTitle("foobar2000");
        if (!string.IsNullOrWhiteSpace(foobarTitle) && foobarTitle.Contains(" - "))
        {
            var clean = Regex.Replace(foobarTitle, @"\s*\[foobar2000.*?\]", "", RegexOptions.IgnoreCase);
            var idx = clean.LastIndexOf(" - ", StringComparison.Ordinal);
            if (idx > 0)
            {
                return (clean[..idx].Trim(), clean[(idx + 3)..].Trim(), "Foobar2000");
            }
        }

        return null;
    }

    private float SampleProcessVolume(string[] processNames)
    {
        lock (_lock)
        {
            if (_audioSessionManager == null) return 0f;

            try
            {
                using var sessionEnumerator = _audioSessionManager.GetSessionEnumerator();
                if (sessionEnumerator == null) return 0f;

                float maxPeak = 0f;
                foreach (AudioSessionControl session in sessionEnumerator)
                {
                    if (session == null) continue;

                    using var sessionControl = session.QueryInterface<AudioSessionControl2>();
                    if (sessionControl?.Process == null) continue;

                    string proc = sessionControl.Process.ProcessName;
                    if (processNames.Any(p => proc.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        using var meter = session.QueryInterface<AudioMeterInformation>();
                        if (meter != null)
                        {
                            maxPeak = Math.Max(maxPeak, meter.PeakValue);
                        }
                    }
                }
                return maxPeak;
            }
            catch
            {
                return 0f;
            }
        }
    }

    private string CleanAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "System";
        if (appId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (appId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
        if (appId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)) return "网易云音乐";
        if (appId.Contains("QQMusic", StringComparison.OrdinalIgnoreCase)) return "QQ音乐";
        if (appId.Contains("kugou", StringComparison.OrdinalIgnoreCase)) return "酷狗音乐";
        if (appId.Contains("soda", StringComparison.OrdinalIgnoreCase) || appId.Contains("汽水", StringComparison.OrdinalIgnoreCase)) return "汽水音乐";
        if (appId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (appId.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        return Path.GetFileNameWithoutExtension(appId);
    }

    private void ResetTrack()
    {
        CurrentTrack = new SystemMediaTrack();
        _prevSignature = "";
        _prevStatus = "None";
        lock (_lock)
        {
            _currentCoverBytes = null;
            _currentCoverHash = null;
        }
        TrackChanged?.Invoke(CurrentTrack);
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _notificationClient?.Dispose();
        _deviceEnumerator?.Dispose();
        _audioSessionManager?.Dispose();
    }
}
#else
using System;
using System.Threading.Tasks;

namespace BiLi_live_Tool.Services.SystemMedia;

public class SystemMediaService : IDisposable
{
    public bool Enabled { get; set; } = false;
    public bool IgnoreBrowsers { get; set; } = true;
    public bool PreferInternalPlayer { get; set; } = true;
    public SystemMediaTrack CurrentTrack { get; } = new();
    public byte[]? CurrentCoverBytes => null;
    public string? CurrentCoverHash => null;
    public event Action<SystemMediaTrack>? TrackChanged;
    public event Action<string>? StatusChanged;
    public event Action<double, double>? ProgressChanged;
    public SystemMediaService(EventHub hub, SongPlayer songPlayer) { }
    public void Start() { }
    public void SimulateTrack(SystemMediaTrack track) { }
    public Task UpdateMediaStateAsync() => Task.CompletedTask;
    public void Dispose() { }
}
#endif
