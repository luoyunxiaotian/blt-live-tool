using Microsoft.JSInterop;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Audio façade for the rebuilt UI (TTS playback, panel sounds, system-speech
/// fallback). Playback is hosted by <see cref="NativeAudio"/> inside this process
/// so OBS's "Application Audio Capture" (WASAPI process loopback) can hear the
/// app — WebView2 renders &lt;audio&gt; in Chromium's own audio-service process,
/// which that capture does not cover. The JS layer (wwwroot/blt-audio.js) stays
/// wired as the fallback for hosts where MediaPlayer is unavailable; the
/// IJSRuntime comes from the Blazor circuit (attached by MainLayout on first
/// render), so the JS path no-ops until then.
/// </summary>
public sealed class AudioService
{
    private readonly NativeAudio _native;
    private IJSRuntime? _js;

    public AudioService(NativeAudio native) => _native = native;

    public void Attach(IJSRuntime js) => _js = js;

    public bool Ready => _js != null;

    /// <summary>True when sound is rendered by this process (i.e. OBS-capturable).</summary>
    public bool NativeActive => _native.TryInit();

    public readonly record struct PlayResult(bool Ok, string Error);

    /// <summary>Plays base64 audio and resolves when playback ends (ok) or fails with a reason.</summary>
    public async Task<PlayResult> PlayBase64Async(string base64, string mime, double volume, double rate = 1.0)
    {
        if (_native.TryInit())
        {
            try
            {
                var r = await _native.PlayBase64Async(Convert.FromBase64String(base64), mime, volume, rate);
                return new PlayResult(r.Ok, r.Error);
            }
            catch (Exception ex)
            {
                return new PlayResult(false, "native-decode:" + ex.Message);
            }
        }

        var js = _js;
        if (js == null) return new PlayResult(false, "no-js");
        try { return await js.InvokeAsync<PlayResult>("bltAudio.playBytes", base64, mime, volume, rate); }
        catch (Exception ex) { return new PlayResult(false, "interop:" + ex.Message); }
    }

    /// <summary>Plays a URL (panel sounds /sounds/*.wav) without occupying the TTS channel.</summary>
    public async Task PlayUrlAsync(string url, double volume)
    {
        if (_native.TryInit())
        {
            await _native.PlayUrlAsync(url, volume);
            return;
        }

        var js = _js;
        if (js == null) return;
        try { await js.InvokeAsync<bool>("bltAudio.playUrl", url, volume); }
        catch { }
    }

    public async Task<bool> SpeakSystemAsync(string text, double rate, double pitch, double volume, string voice = "")
    {
        if (_native.TryInit())
        {
            try { return await _native.SpeakSystemAsync(text, rate, pitch, volume, voice); }
            catch { return false; }
        }

        var js = _js;
        if (js == null) return false;
        try { return await js.InvokeAsync<bool>("bltAudio.speakSys", text, rate, pitch, volume, voice); }
        catch { return false; }
    }

    public async Task StopAsync()
    {
        _native.StopTts();

        // Keep the JS channel quiet too: early calls can land before the native
        // engine exists, and a stop must silence whatever is actually playing.
        var js = _js;
        if (js == null) return;
        try { await js.InvokeVoidAsync("bltAudio.stop"); }
        catch { }
    }
}
