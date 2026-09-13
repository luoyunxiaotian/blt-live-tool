using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.JSInterop;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Port of Bin/public/song-player.js: drives the hidden &lt;audio&gt; element
/// (blt-audio.js), resolves play URLs through /api/song-request/song-url,
/// auto-advances (max 3 consecutive failures), and reports progress to the
/// server so the OBS lyrics overlay follows (song_progress WS frame).
/// </summary>
public sealed class SongPlayer
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly AppConfig _config;
    private readonly EventHub _hub;

    private DotNetObjectReference<SongPlayer>? _self;
    private IJSRuntime? _js;
    private long _lastReportTicks;
    private int _failCount;

    public string CurrentName { get; private set; } = "";
    public string CurrentArtist { get; private set; } = "";
    public string CurrentRequester { get; private set; } = "";
    public string CurrentPlatform { get; private set; } = "";
    public double Current { get; private set; }
    public double Duration { get; private set; }
    public bool IsPlaying { get; private set; }
    public int PlayingIndex { get; private set; } = -1;
    public string LastNote { get; private set; } = "";

    public event Action? Changed;

    public SongPlayer(AppConfig config, EventHub hub)
    {
        _config = config;
        _hub = hub;
    }

    /// <summary>Attaches the Blazor JS runtime + DotNet callback ref (called by MainLayout).</summary>
    public async Task InitAsync(IJSRuntime js)
    {
        _js = js;
        _self ??= DotNetObjectReference.Create(this);
        try { await js.InvokeVoidAsync("bltAudio.setDotNetRef", _self); } catch { }
    }

    // ---------------- controls ----------------

    public async Task PlayIndexAsync(int index)
    {
        if (index < 0) { StopPlayback(); return; }
        try
        {
            var body = new JsonObject { ["index"] = index };
            var resp = await PostJsonAsync("/api/song-request/song-url", body);
            if (resp == null) { Note("解析播放地址失败"); return; }
            var ok = resp["ok"]?.GetValue<bool>() ?? false;
            var url = resp["url"]?.GetValue<string>() ?? "";
            if (!ok || url.Length == 0)
            {
                Note("无法播放：" + (resp["msg"]?.GetValue<string>() is var m && !string.IsNullOrEmpty(m) ? m : "VIP/版权限制"));
                await AutoNextAfterFailureAsync();
                return;
            }
            if (url.StartsWith("/")) url = $"http://127.0.0.1:{_config.Port}{url}";
            var song = resp["song"] as JsonObject;
            CurrentName = song?["name"]?.GetValue<string>() ?? "";
            CurrentArtist = song?["artist"]?.GetValue<string>() ?? "";
            CurrentRequester = song?["requester"]?.GetValue<string>() ?? "";
            CurrentPlatform = song?["platform"]?.GetValue<string>() ?? "";
            PlayingIndex = index;
            _failCount = 0;
            var volume = SongVolume();
            if (_js != null)
            {
                try
                {
                    await _js.InvokeVoidAsync("bltAudio.songLoad", url, volume);
                    await _js.InvokeVoidAsync("bltAudio.songPlay");
                }
                catch { }
            }
            IsPlaying = true;
            Note($"正在播放：{CurrentName}");
            Changed?.Invoke();
            await ReportProgressAsync(force: true);
        }
        catch (Exception ex)
        {
            Note("播放失败：" + ex.Message);
        }
    }

    public async Task PlayNextAsync()
    {
        // Server-side skip advances currentIndex; then play that entry.
        try
        {
            await PostJsonAsync("/api/song-request/skip", new JsonObject());
        }
        catch { }
        var next = PlayingIndex + 1;
        await PlayIndexAsync(next);
    }

    public void Pause()
    {
        _ = _js?.InvokeVoidAsync("bltAudio.songPause");
        IsPlaying = false;
        Changed?.Invoke();
        _ = ReportProgressAsync(force: true);
    }

    public void Resume()
    {
        _ = _js?.InvokeVoidAsync("bltAudio.songPlay");
        IsPlaying = true;
        Changed?.Invoke();
    }

    public void Seek(double t)
    {
        _ = _js?.InvokeVoidAsync("bltAudio.songSeek", t);
        Current = t;
        Changed?.Invoke();
        _ = ReportProgressAsync(force: true);
    }

    public void StopPlayback()
    {
        _ = _js?.InvokeVoidAsync("bltAudio.songStop");
        IsPlaying = false;
        PlayingIndex = -1;
        CurrentName = "";
        Current = 0;
        Duration = 0;
        Changed?.Invoke();
        _ = ReportProgressAsync(force: true);
    }

    private double SongVolume()
    {
        var sr = _config.GetNode("songRequest") as JsonObject;
        var v = sr != null && sr.TryGetPropertyValue("volume", out var vn) && vn is JsonValue vv && vv.TryGetValue<double>(out var dv) ? dv : 0.8;
        return Math.Max(0, Math.Min(1, v));
    }

    private async Task AutoNextAfterFailureAsync()
    {
        var sr = _config.GetNode("songRequest") as JsonObject;
        var autoPlay = sr == null || sr["autoPlay"] is not JsonValue av || !av.TryGetValue<bool>(out var ab) || ab;
        _failCount++;
        if (!autoPlay) return;
        if (_failCount >= 3)
        {
            Note("连续 3 首播放失败，已停止自动播放");
            return;
        }
        await PlayIndexAsync(PlayingIndex + 1);
    }

    // ---------------- JS → C# ----------------

    [JSInvokable]
    public void OnSongEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            Current = root.TryGetProperty("current", out var c) ? c.GetDouble() : Current;
            Duration = root.TryGetProperty("duration", out var d) ? d.GetDouble() : Duration;
            var paused = root.TryGetProperty("paused", out var p) && p.GetBoolean();

            switch (kind)
            {
                case "time":
                    IsPlaying = !paused;
                    // Throttle: 1s playing / 3s paused
                    var now = Environment.TickCount64;
                    var interval = paused ? 3000 : 1000;
                    if (now - _lastReportTicks >= interval)
                        _ = ReportProgressAsync();
                    break;
                case "ended":
                    IsPlaying = false;
                    Changed?.Invoke();
                    _ = OnEndedAsync();
                    return;
                case "error":
                    Note("播放中断");
                    _ = AutoNextAfterFailureAsync();
                    break;
                case "loaded":
                case "playing":
                    IsPlaying = !paused;
                    break;
                case "pause":
                    IsPlaying = false;
                    _ = ReportProgressAsync(force: true);
                    break;
            }
            Changed?.Invoke();
        }
        catch { }
    }

    private async Task OnEndedAsync()
    {
        await ReportProgressAsync(force: true);
        var sr = _config.GetNode("songRequest") as JsonObject;
        var autoPlay = sr == null || sr["autoPlay"] is not JsonValue av || !av.TryGetValue<bool>(out var ab) || ab;
        if (autoPlay) await PlayIndexAsync(PlayingIndex + 1);
    }

    // ---------------- progress reporting (OBS lyrics overlay) ----------------

    private async Task ReportProgressAsync(bool force = false)
    {
        _lastReportTicks = Environment.TickCount64;
        try
        {
            var data = new JsonObject
            {
                ["name"] = CurrentName,
                ["artist"] = CurrentArtist,
                ["requester"] = CurrentRequester,
                ["platform"] = CurrentPlatform,
                ["current"] = Math.Round(Current * 10) / 10,
                ["duration"] = Math.Round(Duration * 10) / 10,
                ["playing"] = IsPlaying,
                ["index"] = PlayingIndex,
            };
            var body = new JsonObject { ["data"] = data };
            await PostJsonAsync("/api/song-request/progress", body);
        }
        catch { }
    }

    private void Note(string msg)
    {
        LastNote = msg;
        Changed?.Invoke();
    }

    private async Task<JsonNode?> PostJsonAsync(string path, JsonObject body)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync($"http://127.0.0.1:{_config.Port}{path}", content);
        var txt = await resp.Content.ReadAsStringAsync();
        try { return JsonNode.Parse(txt); } catch { return null; }
    }
}
