using System.Text.Json.Nodes;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Port of alertPanelSound (Bin/public/app.js): plays the alert sound files in
/// the control panel itself (independent from TTS). Conditions mirror the
/// original: alert enabled + type switch on + that type has a sound chosen,
/// with its own volume; alert_test frames force-play for testing.
/// </summary>
public sealed class PanelSoundPlayer
{
    private readonly AppConfig _config;
    private readonly EventHub _hub;
    private readonly AudioService _audio;

    public PanelSoundPlayer(AppConfig config, EventHub hub, AudioService audio)
    {
        _config = config;
        _hub = hub;
        _audio = audio;
    }

    public void Start()
    {
        _hub.OnEvent += OnEvent;
        _hub.OnOutbound += OnOutbound;
    }

    public void Stop()
    {
        _hub.OnEvent -= OnEvent;
        _hub.OnOutbound -= OnOutbound;
    }

    private void OnEvent(LiveEvent ev)
    {
        try
        {
            var alert = _config.GetNode("alert") as JsonObject;
            if (alert == null) return;
            if (!Flag(alert, "enabled", true)) return;
            if (!Flag(alert, "panelSound", true)) return;   // 面板提示音总开关（原版语义）
            var key = MapKey(ev.Type, ev.MsgType);
            if (key == null) return;
            if (alert["types"] is not JsonObject types) return;
            if (types[key] is not JsonObject t) return;
            if (!Flag(t, "on")) return;
            var sound = Str(t, "sound");
            if (sound.Length == 0) return;
            var volume = Num(alert, "panelVolume", 0.6);
            Play(sound, volume);
        }
        catch { }
    }

    private void OnOutbound(string type, object? data)
    {
        if (type != "alert_test") return;
        try
        {
            // Force-play on test frames (bypass switches, like the original).
            var ev = data as LiveEvent;
            var key = ev == null ? "gift" : MapKey(ev.Type, ev.MsgType) ?? "gift";
            var alert = _config.GetNode("alert") as JsonObject;
            string sound = "";
            if (alert?["types"] is JsonObject types && types[key] is JsonObject t)
                sound = Str(t, "sound");
            if (sound.Length == 0)
                sound = key switch { "gift" => "gift.wav", "guard" => "guard.wav", "superchat" => "superchat.wav", _ => "" };
            if (sound.Length == 0) return;
            var volume = alert is null ? 0.6 : Num(alert, "panelVolume", 0.6);
            Play(sound, volume);
        }
        catch { }
    }

    private static string? MapKey(string type, int msgType) => type switch
    {
        "gifts" or "gifts_merged" => "gift",
        "guard" => "guard",
        "superchat" => "superchat",
        "interact" => msgType == 2 ? "follow" : "enter",
        _ => null,
    };

    private void Play(string fileName, double volume)
    {
        var url = $"http://127.0.0.1:{_config.Port}/sounds/{Uri.EscapeDataString(fileName ?? "")}";
        _ = _audio.PlayUrlAsync(url, volume);
    }

    private static bool Flag(JsonObject o, string key, bool def = false)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<bool>(out var b) ? b : def;

    private static string Str(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<string>(out var s) ? s ?? "" : "";

    private static double Num(JsonObject o, string key, double def)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<double>(out var d) ? d : def;
}
