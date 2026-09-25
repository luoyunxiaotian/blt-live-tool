// NativeAudio.cs — in-process audio engine (WinRT MediaPlayer).
//
// Why it exists: WebView2 renders <audio> sound inside Chromium's own sandboxed
// utility process (msedgewebview2.exe --utility-sub-type=audio.mojom.AudioService),
// which is a grandchild of this app. The WASAPI session therefore belongs to
// msedgewebview2.exe, and OBS's "Application Audio Capture" — process loopback
// aimed at the exe it is pointed at — recorded silence when pointed at
// BiLi_live_Tool.exe. MediaPlayer creates the session inside the calling process
// (verified with an audio-session enumeration: the session PID equals the host
// PID), so OBS can capture the app's audio directly.
//
// The public surface mirrors wwwroot/blt-audio.js so the legacy JS layer stays
// usable as a fallback; song events are emitted as the same JSON string the JS
// layer used to hand to SongPlayer.OnSongEvent.

#if WINDOWS
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Audio playback hosted in this process instead of the WebView2 audio service.
/// </summary>
public sealed class NativeAudio : IDisposable
{
    public readonly record struct PlayResult(bool Ok, string Error);

    /// <summary>Result returned when the engine could not be created at all.</summary>
    public const string Unavailable = "native-unavailable";

    private const int PanelVoices = 3;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly object _gate = new();
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "blt-audio");

    // TTS channel: one utterance at a time, awaited to completion.
    private MediaPlayer? _tts;
    private InMemoryRandomAccessStream? _ttsMem;
    private SpeechSynthesizer? _ttsSynth;
    private string _ttsTemp = "";
    private TaskCompletionSource<PlayResult>? _ttsWait;

    // Panel sounds: short and allowed to overlap each other, and never silenced
    // by stop() — blt-audio.js used a fresh Audio element per cue as well.
    private MediaPlayer[] _panel = Array.Empty<MediaPlayer>();
    private int _panelNext;

    // Song channel: mirrors the hidden <audio> element blt-audio.js drove.
    private MediaPlayer? _song;
    private string _songTemp = "";
    private string _songUrl = "";
    private bool _songStopped = true;
    private bool _songOpened;
    private bool _songRetried;
    private Timer? _ticker;

    private bool _initDone;
    private bool _available;

    /// <summary>Same JSON payload the JS layer passed to SongPlayer.OnSongEvent.</summary>
    public event Action<string>? SongEventJson;

    /// <summary>
    /// Creates the players on first use. Returns false when the platform refuses
    /// to host MediaPlayer, in which case callers fall back to the JS layer.
    /// </summary>
    public bool TryInit()
    {
        if (_initDone) return _available;
        _initDone = true;
        try
        {
            _tts = NewPlayer();
            _tts.MediaEnded += (_, _) => FinishTts(new PlayResult(true, ""));
            _tts.MediaFailed += (_, e) => FinishTts(new PlayResult(false, Fail("media", e)));

            _panel = new MediaPlayer[PanelVoices];
            for (var i = 0; i < _panel.Length; i++) _panel[i] = NewPlayer();

            _song = NewPlayer();
            _song.MediaOpened += (_, _) => { _songOpened = true; Emit("loaded"); };
            _song.MediaEnded += (_, _) => { if (!_songStopped) Emit("ended"); };
            _song.MediaFailed += (_, e) => OnSongFailed(e);
            _song.PlaybackSession.PlaybackStateChanged += (_, _) =>
            {
                if (!_songStopped && _song!.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) Emit("playing");
            };

            _available = true;
        }
        catch
        {
            _available = false;
        }
        return _available;
    }

    // ---------------- TTS / system speech ----------------

    /// <summary>Plays encoded audio bytes; resolves when playback ends or is stopped.</summary>
    public async Task<PlayResult> PlayBase64Async(byte[] data, string mime, double volume, double rate)
    {
        if (!TryInit() || _tts == null) return new PlayResult(false, Unavailable);
        StopTts();

        MediaSource source;
        InMemoryRandomAccessStream mem;
        try
        {
            (source, mem) = await SourceFromBytesAsync(data, mime);
        }
        catch (Exception ex)
        {
            return new PlayResult(false, "native:" + ex.Message);
        }

        var tcs = new TaskCompletionSource<PlayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _ttsWait = tcs;
        try
        {
            _tts.Volume = Clamp01(volume);
            TrySetRate(_tts, rate);
            _ttsMem = mem;
            _tts.Source = source;
            _tts.Play();
        }
        catch (Exception ex)
        {
            lock (_gate) _ttsWait = null;
            return new PlayResult(false, "native:" + ex.Message);
        }

        // A cancelled utterance resolves immediately; the cap only guards against a
        // decoder that never reports end/failure and would otherwise stall the queue.
        try { return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(3)); }
        catch (TimeoutException) { return new PlayResult(false, "native:timeout"); }
    }

    /// <summary>
    /// One voice installed in Windows, as offered under the &#34;系统音色&#34; group.
    /// <c>Name</c> is the display name because that is what the config stores (the
    /// registry-style token id is only kept for lookup).
    /// </summary>
    public readonly record struct SystemVoice(string Name, string Label, string Lang, string Id);

    /// <summary>
    /// Voices the sys engine can use. They come from the OS (not from an engine
    /// process), which is why the picker cannot read them over the TTS proxy.
    /// </summary>
    public static IReadOnlyList<SystemVoice> SystemVoiceList()
    {
        var list = new List<SystemVoice>();
        try
        {
            foreach (var v in SpeechSynthesizer.AllVoices)
            {
                var gender = v.Gender == VoiceGender.Female ? "女" : v.Gender == VoiceGender.Male ? "男" : "?";
                var lang = string.IsNullOrEmpty(v.Language) ? "" : v.Language;
                var name = string.IsNullOrEmpty(v.DisplayName) ? v.Id : v.DisplayName;
                list.Add(new SystemVoice(name, name + "（" + gender + (lang.Length > 0 ? " · " + lang : "") + "）", lang, v.Id));
            }
        }
        catch
        {
            // No speech platform on this machine → an empty list, not a crash.
        }
        return list;
    }

    /// <summary>
    /// Speaks text with a system voice, rendering into this process. When
    /// <paramref name="voice"/> names an installed voice it is used, otherwise a
    /// Chinese voice is preferred and the system default is the last resort.
    /// </summary>
    public async Task<bool> SpeakSystemAsync(string text, double rate, double pitch, double volume, string voice = "")
    {
        if (!TryInit() || _tts == null) return false;
        StopTts();
        try
        {
            var synth = new SpeechSynthesizer();
            try
            {
                var picked = PickSystemVoice(voice);
                if (picked != null) synth.Voice = picked;
            }
            catch { /* keep the system default voice */ }
            try
            {
                synth.Options.SpeakingRate = Math.Clamp(rate, 0.5, 2);
                synth.Options.AudioPitch = Math.Clamp(pitch, 0.5, 2);
                synth.Options.AudioVolume = Clamp01(volume);
            }
            catch { /* voice does not expose options; defaults are fine */ }

            var stream = await synth.SynthesizeTextToStreamAsync(text ?? "");
            var tcs = new TaskCompletionSource<PlayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _ttsWait = tcs;
            _ttsSynth = synth;   // must outlive the stream
            _tts.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _tts.Play();
            var res = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(3));
            return res.Ok;
        }
        catch
        {
            FinishTts(new PlayResult(false, "native:speech"));
            return false;
        }
    }

    /// <summary>Stops the current utterance (a stop is not a playback failure).</summary>
    public void StopTts()
    {
        TaskCompletionSource<PlayResult>? w;
        lock (_gate) { w = _ttsWait; _ttsWait = null; }
        try { _tts?.Pause(); } catch { }
        try { if (_tts != null) _tts.Source = null; } catch { }
        _ttsMem?.Dispose();
        _ttsMem = null;
        CleanupTemp(ref _ttsTemp);
        try { _ttsSynth?.Dispose(); } catch { }
        _ttsSynth = null;
        w?.TrySetResult(new PlayResult(true, ""));
    }

    /// <summary>Fire-and-forget cue (panel alert sounds); overlaps the TTS channel.</summary>
    public Task PlayUrlAsync(string url, double volume)
    {
        if (!TryInit() || _panel.Length == 0) return Task.CompletedTask;
        var slot = _panelNext++ % _panel.Length;
        try
        {
            var p = _panel[slot];
            p.Volume = Clamp01(volume);
            p.Source = MediaSource.CreateFromUri(new Uri(url));
            p.Play();
        }
        catch
        {
            // A cue that cannot play is not worth reporting to the caller.
        }
        return Task.CompletedTask;
    }

    // ---------------- song channel ----------------

    public bool SongLoad(string url, double volume)
    {
        if (!TryInit() || _song == null) return false;
        _songStopped = false;
        _songOpened = false;
        _songRetried = false;
        _songUrl = url;
        CleanupTemp(ref _songTemp);
        try
        {
            _song.Volume = Clamp01(volume);
            _song.Source = MediaSource.CreateFromUri(new Uri(url));
            StartTicker();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SongPlay()
    {
        if (_song == null) return;
        _songStopped = false;
        try { _song.Play(); } catch { Emit("error"); }
    }

    public void SongPause()
    {
        if (_song == null) return;
        try { _song.Pause(); } catch { }
        Emit("pause");
    }

    public void SongSeek(double t)
    {
        if (_song == null) return;
        try { _song.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, t)); } catch { }
    }

    public void SongSetVolume(double v)
    {
        if (_song == null) return;
        try { _song.Volume = Clamp01(v); } catch { }
    }

    /// <summary>Manual stop: suppresses the teardown events so the player cannot auto-advance.</summary>
    public void SongStop()
    {
        _songStopped = true;
        try { _song?.Pause(); } catch { }
        try { if (_song != null) _song.Source = null; } catch { }
        _songOpened = false;
        CleanupTemp(ref _songTemp);
    }

    /// <summary>Same shape bltAudio.songState() returned.</summary>
    public (double Current, double Duration, bool Paused) SongState()
    {
        if (_song == null) return (0, 0, true);
        try
        {
            var s = _song.PlaybackSession;
            var dur = s.NaturalDuration.TotalSeconds;
            return (s.Position.TotalSeconds, double.IsNaN(dur) || double.IsInfinity(dur) ? 0 : dur,
                    s.PlaybackState != MediaPlaybackState.Playing);
        }
        catch
        {
            return (0, 0, true);
        }
    }

    // ---------------- internals ----------------

    private static MediaPlayer NewPlayer()
    {
        var p = new MediaPlayer { AutoPlay = false };
        // Without this the player registers with the system media transport controls,
        // which is pointless here and can keep a phantom session alive.
        try { p.CommandManager.IsEnabled = false; } catch { }
        return p;
    }

    private static double Clamp01(double v) => double.IsNaN(v) ? 1 : Math.Max(0, Math.Min(1, v));

    /// <summary>Matches a configured voice against the installed ones (id or display name).</summary>
    private static VoiceInformation? PickSystemVoice(string voice)
    {
        var all = SpeechSynthesizer.AllVoices;
        if (!string.IsNullOrWhiteSpace(voice))
        {
            foreach (var v in all)
                if (string.Equals(v.Id, voice, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v.DisplayName, voice, StringComparison.OrdinalIgnoreCase))
                    return v;
        }
        return all.FirstOrDefault(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
               ?? (all.Count > 0 ? all[0] : null);
    }

    private static void TrySetRate(MediaPlayer p, double rate)
    {
        if (!(Math.Abs(rate - 1) > 0.001)) return;
        try { p.PlaybackSession.PlaybackRate = Math.Clamp(rate, 0.5, 2); } catch { }
    }

    private static async Task<(MediaSource Source, InMemoryRandomAccessStream Stream)> SourceFromBytesAsync(byte[] data, string mime)
    {
        var ras = new InMemoryRandomAccessStream();
        var dw = new DataWriter(ras);
        dw.WriteBytes(data);
        await dw.StoreAsync();
        // DetachStream keeps Dispose from closing the stream the player is reading.
        dw.DetachStream();
        dw.Dispose();
        ras.Seek(0);
        return (MediaSource.CreateFromStream(ras, string.IsNullOrEmpty(mime) ? "audio/mpeg" : mime), ras);
    }

    private void FinishTts(PlayResult r)
    {
        TaskCompletionSource<PlayResult>? w;
        lock (_gate) { w = _ttsWait; _ttsWait = null; }
        w?.TrySetResult(r);
    }

    private static string Fail(string prefix, MediaPlayerFailedEventArgs e)
    {
        var msg = e.ErrorMessage ?? "";
        var code = e.ExtendedErrorCode != null ? e.ExtendedErrorCode.Message : "";
        return prefix + ":" + e.Error + (msg.Length > 0 ? " " + msg : "") + (code.Length > 0 ? " (" + code + ")" : "");
    }

    private void OnSongFailed(MediaPlayerFailedEventArgs e)
    {
        if (_songStopped || _song == null) return;
        // Media Foundation's HTTP source occasionally refuses a CDN URL that a plain
        // download handles; retry once from a local copy before reporting an error.
        if (!_songOpened && !_songRetried && _songUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _songRetried = true;
            _ = RetrySongFromDownloadAsync();
            return;
        }
        Emit("error");
    }

    private async Task RetrySongFromDownloadAsync()
    {
        var path = "";
        try
        {
            Directory.CreateDirectory(_tmpDir);
            path = Path.Combine(_tmpDir, "song-" + Guid.NewGuid().ToString("N")[..8] + ".mp3");
            using (var resp = await Http.GetAsync(_songUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(path);
                await src.CopyToAsync(dst);
            }
            if (_songStopped || _song == null) { try { File.Delete(path); } catch { } return; }
            _songTemp = path;
            _song.Source = MediaSource.CreateFromUri(new Uri(path));
            _song.Play();
        }
        catch
        {
            if (path.Length > 0) { try { File.Delete(path); } catch { } }
            Emit("error");
        }
    }

    private void StartTicker()
    {
        _ticker ??= new Timer(_ =>
        {
            if (!_songStopped && _songOpened) Emit("time");
        }, null, 1000, 1000);
    }

    private void Emit(string kind)
    {
        var handler = SongEventJson;
        if (handler == null) return;
        var (cur, dur, paused) = SongState();
        var json = JsonSerializer.Serialize(new
        {
            current = cur,
            duration = dur,
            paused,
            kind,
        });
        // SongPlayer updates Blazor state from this, so it must land on the UI thread
        // (MediaPlayer raises its events on a worker thread).
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { handler(json); } catch { }
        });
    }

    private static void CleanupTemp(ref string path)
    {
        if (path.Length == 0) return;
        try { File.Delete(path); } catch { }
        path = "";
    }

    public void Dispose()
    {
        try { _ticker?.Dispose(); } catch { }
        _ticker = null;
        StopTts();
        foreach (var p in _panel) { try { p.Dispose(); } catch { } }
        try { _song?.Dispose(); } catch { }
        try { _tts?.Dispose(); } catch { }
        CleanupTemp(ref _songTemp);
    }
}
#else
namespace BiLi_live_Tool.Services;

/// <summary>
/// Non-Windows stub: MediaPlayer is a Windows API. Same public surface as the
/// Windows implementation so shared wiring compiles for every target framework;
/// every call reports "unavailable" and callers keep using the JS audio layer.
/// </summary>
public sealed class NativeAudio : IDisposable
{
    public readonly record struct PlayResult(bool Ok, string Error);

    public const string Unavailable = "native-unavailable";

    public readonly record struct SystemVoice(string Name, string Label, string Lang, string Id);

    public static IReadOnlyList<SystemVoice> SystemVoiceList() => Array.Empty<SystemVoice>();

    public event Action<string>? SongEventJson { add { } remove { } }

    public bool TryInit() => false;

    public Task<PlayResult> PlayBase64Async(byte[] data, string mime, double volume, double rate)
        => Task.FromResult(new PlayResult(false, Unavailable));

    public Task<bool> SpeakSystemAsync(string text, double rate, double pitch, double volume, string voice = "")
        => Task.FromResult(false);

    public void StopTts() { }

    public Task PlayUrlAsync(string url, double volume) => Task.CompletedTask;

    public bool SongLoad(string url, double volume) => false;

    public void SongPlay() { }

    public void SongPause() { }

    public void SongSeek(double t) { }

    public void SongSetVolume(double v) { }

    public void SongStop() { }

    public (double Current, double Duration, bool Paused) SongState() => (0, 0, true);

    public void Dispose() { }
}
#endif
