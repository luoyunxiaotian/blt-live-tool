using Microsoft.JSInterop;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Bridge to wwwroot/blt-audio.js — the audio layer for the rebuilt UI
/// (TTS playback, panel sounds, system-speech fallback). The IJSRuntime comes
/// from the Blazor circuit (attached by MainLayout on first render), so all
/// calls no-op until then.
/// </summary>
public sealed class AudioService
{
    private IJSRuntime? _js;

    public void Attach(IJSRuntime js) => _js = js;

    public bool Ready => _js != null;

    public readonly record struct PlayResult(bool Ok, string Error);

    /// <summary>Plays base64 audio and resolves when playback ends (ok) or fails with a reason.</summary>
    public async Task<PlayResult> PlayBase64Async(string base64, string mime, double volume, double rate = 1.0)
    {
        var js = _js;
        if (js == null) return new PlayResult(false, "no-js");
        try { return await js.InvokeAsync<PlayResult>("bltAudio.playBytes", base64, mime, volume, rate); }
        catch (Exception ex) { return new PlayResult(false, "interop:" + ex.Message); }
    }

    /// <summary>Plays a URL (panel sounds /sounds/*.wav) without occupying the TTS channel.</summary>
    public async Task PlayUrlAsync(string url, double volume)
    {
        var js = _js;
        if (js == null) return;
        try { await js.InvokeAsync<bool>("bltAudio.playUrl", url, volume); }
        catch { }
    }

    public async Task<bool> SpeakSystemAsync(string text, double rate, double pitch, double volume)
    {
        var js = _js;
        if (js == null) return false;
        try { return await js.InvokeAsync<bool>("bltAudio.speakSys", text, rate, pitch, volume); }
        catch { return false; }
    }

    public async Task StopAsync()
    {
        var js = _js;
        if (js == null) return;
        try { await js.InvokeVoidAsync("bltAudio.stop"); }
        catch { }
    }
}
