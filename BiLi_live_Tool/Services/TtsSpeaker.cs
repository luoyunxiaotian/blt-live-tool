using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Port of Bin/public/tts.js into the app process: filters events, builds the
/// spoken text (Chinese numerals for gift counts), queues them (guard priority,
/// queueMax), resolves tone presets and volume, then synthesizes + plays via
/// the embedded TTS engines with the original fallback chain
/// (moss → edge → system speech, demoted after 2 consecutive failures).
/// Runs in the Blazor UI host; audio playback goes through AudioService.
/// </summary>
public sealed class TtsSpeaker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private readonly AppConfig _config;
    private readonly EventHub _hub;
    private readonly AudioService _audio;

    private readonly object _lock = new();
    private readonly List<Item> _queue = new();
    private bool _speaking;
    private string _currentText = "";
    private readonly Dictionary<string, int> _fails = new();
    private string? _effectiveEngine;
    private volatile bool _skipRequested;

    // Diagnostics for /api/maui/debug/tray
    public string LastResult { get; private set; } = "not-run";
    public string LastEngineError { get; private set; } = "";

    public event Action? Changed;

    private sealed record Item(string Text, string TypeKey, int Priority, bool IsGuard);

    private sealed class TypeCfg
    {
        public bool Enabled = true;
        public bool SayUid;
        public long MinMedal, MinHonor;
        public double Volume = 1;
        public List<string> Texts = new();
    }

    private sealed class TonePreset
    {
        public string Label = "";
        public double Rate = 1;
        public double PitchHz;
        public string Voice = "";
        public string Prefix = "";
    }

    private sealed class Settings
    {
        public bool Enabled;
        public string Engine = "edge";
        public string Voice = "";
        public string MossVoice = "Junhao";
        public double Rate = 1, Pitch = 1, Volume = 1, Gain = 0, Gap = 0;
        public int QueueMax = 8;
        public bool GuardPriority = true;
        public string BlacklistUids = "", BannedWords = "";
        public Dictionary<string, TypeCfg> Types = new();
        public Dictionary<string, TonePreset> Presets = new();
        public string ToneGlobal = "normal";
        public Dictionary<string, string> ToneByType = new();
    }

    private Settings? _cfg;
    private long _cfgTime;

    public TtsSpeaker(AppConfig config, EventHub hub, AudioService audio)
    {
        _config = config;
        _hub = hub;
        _audio = audio;
    }

    public void Start() => _hub.OnEvent += OnEvent;
    public void Stop() => _hub.OnEvent -= OnEvent;

    public int QueueCount { get { lock (_lock) return _queue.Count; } }
    public bool Speaking { get { lock (_lock) return _speaking; } }
    public string CurrentText { get { lock (_lock) return _currentText; } }

    /// <summary>One pending queue item, as shown in the panel queue list.</summary>
    public sealed record QueueItem(string Type, string Text, bool Guard, int Priority);

    /// <summary>Pending items for the panel queue list (oldest first).</summary>
    public List<QueueItem> QueueSnapshot()
    {
        lock (_lock)
            return _queue.Select(i => new QueueItem(i.TypeKey, i.Text, i.IsGuard, i.Priority)).ToList();
    }

    /// <summary>
    /// Legacy 「⏭ 跳过当前」: stop the item being spoken and move straight to the
    /// next one. The stop reaches the audio layer as a failed play, so the skip
    /// flag keeps it out of the engine-demotion counter.
    /// </summary>
    public void SkipCurrent()
    {
        _skipRequested = true;
        try { _ = _audio.StopAsync(); } catch { }
        Changed?.Invoke();
    }

    /// <summary>Drops everything still waiting (the item being spoken keeps playing).</summary>
    public void ClearQueue()
    {
        lock (_lock) _queue.Clear();
        Changed?.Invoke();
    }

    // ---------------- event intake ----------------

    private void OnEvent(LiveEvent ev)
    {
        try
        {
            var cfg = Settings2s();
            if (!cfg.Enabled) return;
            var typeKey = ev.Type switch
            {
                "danmu" => "danmu",
                "gifts" or "gifts_merged" => "gift",
                "superchat" => "superchat",
                "interact" or "guard" => "welcome",
                _ => null,
            };
            if (typeKey == null) return;
            if (!ShouldSpeak(cfg, typeKey, ev)) return;
            var text = BuildText(cfg, typeKey, ev);
            if (string.IsNullOrWhiteSpace(text)) return;
            Enqueue(cfg, text, typeKey, ev);
            _ = PumpAsync();
        }
        catch { }
    }

    private static bool ShouldSpeak(Settings cfg, string typeKey, LiveEvent ev)
    {
        if (!cfg.Types.TryGetValue(typeKey, out var tc) || !tc.Enabled) return false;
        if (cfg.BlacklistUids.Length > 0 && ev.Uid.Length > 0)
        {
            var uids = Regex.Split(cfg.BlacklistUids, "[,，\\s]+").Where(s => s.Length > 0);
            if (uids.Contains(ev.Uid)) return false;
        }
        if (cfg.BannedWords.Length > 0 && ev.Msg.Length > 0)
        {
            foreach (var w in Regex.Split(cfg.BannedWords, "[,，]+").Where(s => s.Length > 0))
                if (ev.Msg.Contains(w, StringComparison.Ordinal)) return false;
        }
        if (tc.MinMedal > 0 && ev.MedalLevel < tc.MinMedal) return false;
        if (tc.MinHonor > 0 && ev.HonorLevel < tc.MinHonor) return false;
        return true;
    }

    private static string BuildText(Settings cfg, string typeKey, LiveEvent ev)
    {
        cfg.Types.TryGetValue(typeKey, out var tc);
        // 「念昵称」开关（UID 数字永不念，隐私）
        var uname = (tc?.SayUid == true && !string.IsNullOrWhiteSpace(ev.Uname)) ? ev.Uname.Trim() : "";

        switch (typeKey)
        {
            case "danmu":
                return uname.Length > 0 ? uname + "说：" + ev.Msg : "说：" + ev.Msg;

            case "gift":
            {
                if (ev.Type == "gifts_merged")
                {
                    // 汇总事件：小花花五个、牛哇牛哇七个
                    var parts = (ev.Gifts ?? new List<GiftItem>())
                        .Select(g =>
                        {
                            var n = Math.Max(1, g.Num);
                            var name = string.IsNullOrEmpty(g.GiftName) ? "礼物" : g.GiftName;
                            return name + (n > 1 ? NumToCn(n) + "个" : "");
                        })
                        .Where(s => s.Length > 0)
                        .ToList();
                    if (parts.Count == 0) return "";
                    var body = string.Join("、", parts);
                    return uname.Length > 0 ? uname + "送出" + body : "送出" + body;
                }
                var gname = string.IsNullOrEmpty(ev.GiftName) ? "礼物" : ev.GiftName;
                var numTxt = ev.Num > 1 ? NumToCn(ev.Num) + "个" : "";
                return uname.Length > 0 ? uname + "送出" + gname + numTxt : "送出" + gname + numTxt;
            }

            case "superchat":
                return "醒目留言 " + (uname.Length > 0 ? uname + "：" : "") + ev.Msg;

            case "welcome":
            {
                if (uname.Length == 0) return "";
                var pool = (tc?.Texts != null && tc.Texts.Count > 0)
                    ? tc.Texts
                    : new List<string> { "欢迎 {uname} 进入直播间", "{uname} 来啦，欢迎欢迎", "欢迎 {uname} 的到来" };
                var tpl = pool[Random.Shared.Next(pool.Count)];
                return tpl.Replace("{uname}", uname);
            }
        }
        return "";
    }

    private void Enqueue(Settings cfg, string text, string typeKey, LiveEvent ev)
    {
        var isGuard = ev.IsGuard || ev.GuardLevel >= 1 || ev.Type == "guard";
        var item = new Item(text, typeKey, cfg.GuardPriority && isGuard ? 2 : 1, isGuard);
        lock (_lock)
        {
            if (_queue.Count >= (cfg.QueueMax > 0 ? cfg.QueueMax : 8)) return;
            if (item.Priority == 2)
            {
                var idx = 0;
                while (idx < _queue.Count && _queue[idx].Priority == 2) idx++;
                _queue.Insert(idx, item);
            }
            else
            {
                _queue.Add(item);
            }
        }
        Changed?.Invoke();
    }

    // ---------------- playback pump ----------------

    private async Task PumpAsync()
    {
        lock (_lock)
        {
            if (_speaking) return;
            _speaking = true;
        }
        try
        {
            while (true)
            {
                Item? item = null;
                lock (_lock)
                {
                    if (_queue.Count == 0) break;
                    item = _queue[0];
                    _queue.RemoveAt(0);
                    _currentText = item.Text;
                }
                Changed?.Invoke();
                await SpeakItemAsync(item);
                if (_skipRequested)
                {
                    // Skipped by the user: jump straight to the next item.
                    _skipRequested = false;
                    Changed?.Invoke();
                    continue;
                }
                var gap = (int)Math.Clamp(Settings2s().Gap, 0, 5000);
                if (gap > 0) await Task.Delay(gap);
            }
        }
        finally
        {
            lock (_lock)
            {
                _speaking = false;
                _currentText = "";
            }
            Changed?.Invoke();
        }
    }

    private async Task SpeakItemAsync(Item item)
    {
        try
        {
            var cfg = Settings2s();
            var (rate, pitchHz, voice, prefix) = ResolveTone(cfg, item.TypeKey);
            var text = prefix + item.Text;
            var volume = ComputeVolume(cfg, item.TypeKey);

            var engine = _effectiveEngine ?? cfg.Engine;
            var ok = await TryEngineAsync(engine, text, voice, cfg, rate, pitchHz, volume);
            if (!ok && _skipRequested)
            {
                // The user skipped it — not an engine failure, so no demotion.
                LastResult = "skip";
                return;
            }
            if (!ok)
            {
                _fails.TryGetValue(engine, out var f);
                f++;
                _fails[engine] = f;
                if (f >= 2)
                {
                    _fails[engine] = 0;
                    var next = Next(engine);
                    _effectiveEngine = next;
                    LastResult = $"demote {engine}->{next}";
                    // 本条立即降级重试一次
                    ok = await TryEngineAsync(next, text, voice, cfg, rate, pitchHz, volume);
                }
            }
            else
            {
                _fails[engine] = 0;
                LastResult = "ok:" + engine;
            }
        }
        catch (Exception ex)
        {
            LastResult = "error: " + ex.Message;
        }
    }

    private static string Next(string engine) => engine switch
    {
        "moss" => "edge",
        "edge" => "sys",
        _ => "sys",
    };

    private async Task<bool> TryEngineAsync(string engine, string text, string voice, Settings cfg, double rate, double pitchHz, double volume)
    {
        try
        {
            switch (engine)
            {
                case "sys":
                    // 系统语音：pitch 是倍数（1 ± Hz/50）
                    return await _audio.SpeakSystemAsync(text, rate, Math.Clamp(1 + pitchHz / 50.0, 0.5, 2), Math.Min(1, volume));

                case "moss":
                {
                    var body = new JsonObject { ["text"] = text, ["speed"] = Math.Clamp(rate, 0.5, 2) };
                    if (!string.IsNullOrEmpty(cfg.MossVoice)) body["voice"] = cfg.MossVoice;
                    var bytes = await PostSynthAsync("moss", body);
                    if (bytes == null || bytes.Length < 100) { LastResult = "moss:synth-fail"; LastEngineError = "moss:synth-fail"; return false; }
                    var mossPlay = await _audio.PlayBase64Async(Convert.ToBase64String(bytes), "audio/wav", volume, 1);
                    if (!mossPlay.Ok) { LastResult = "moss:play:" + mossPlay.Error; LastEngineError = LastResult; }
                    return mossPlay.Ok;
                }

                default: // edge
                {
                    var v = voice.Length > 0 ? voice : cfg.Voice;
                    if (string.IsNullOrEmpty(v)) v = "zh-CN-XiaoxiaoNeural";
                    var body = new JsonObject
                    {
                        ["text"] = text,
                        ["voice"] = v,
                        ["rate"] = RateStr(rate),
                        ["pitch"] = PitchStr(pitchHz),
                        ["volume"] = "+0%",
                    };
                    var bytes = await PostSynthAsync("edge", body);
                    if (bytes == null || bytes.Length < 100) { LastResult = "edge:synth-fail"; return false; }
                    var edgePlay = await _audio.PlayBase64Async(Convert.ToBase64String(bytes), "audio/mpeg", volume, 1);
                    if (!edgePlay.Ok) { LastResult = "edge:play:" + edgePlay.Error; LastEngineError = LastResult; }
                    return edgePlay.Ok;
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private async Task<byte[]?> PostSynthAsync(string engine, JsonObject body)
    {
        try
        {
            var url = $"http://127.0.0.1:{_config.Port}/api/tts/{engine}/";
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = "";
                try { detail = (await resp.Content.ReadAsStringAsync()).Trim(); } catch { }
                if (detail.Length > 160) detail = detail[..160];
                LastEngineError = $"synth http {(int)resp.StatusCode} {detail}";
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 100) LastEngineError = $"synth small {bytes.Length}";
            return bytes;
        }
        catch (Exception ex)
        {
            LastEngineError = "synth ex: " + ex.GetType().Name + " " + ex.Message;
            return null;
        }
    }

    private static string RateStr(double rate)
    {
        if (rate > 1) return "+" + (int)Math.Round((rate - 1) * 100) + "%";
        if (rate < 1) return "-" + (int)Math.Round((1 - rate) * 100) + "%";
        return "+0%";
    }

    private static string PitchStr(double pitchHz)
    {
        var hz = (int)Math.Round(pitchHz);
        return hz >= 0 ? "+" + hz + "Hz" : hz + "Hz";
    }

    private static (double Rate, double PitchHz, string Voice, string Prefix) ResolveTone(Settings cfg, string typeKey)
    {
        var key = cfg.ToneByType.TryGetValue(typeKey, out var bt) && !string.IsNullOrEmpty(bt) ? bt : cfg.ToneGlobal;
        if (key.Length == 0) key = "normal";
        if (!cfg.Presets.TryGetValue(key, out var preset))
            return (1, 0, "", "");
        return (preset.Rate, preset.PitchHz, preset.Voice, preset.Prefix);
    }

    private static double ComputeVolume(Settings cfg, string typeKey)
    {
        var t = cfg.Types.TryGetValue(typeKey, out var tc) ? tc.Volume : 1;
        var vol = cfg.Volume * t * (1 + cfg.Gain / 100.0);
        return Math.Max(0, Math.Min(1, vol));
    }

    // ---------------- config parsing (2s cache) ----------------

    private Settings Settings2s()
    {
        var now = Environment.TickCount64;
        if (_cfg != null && now - _cfgTime < 2000) return _cfg;
        _cfg = ParseConfig(_config.GetNode("tts") as JsonObject);
        _cfgTime = now;
        return _cfg;
    }

    private static Settings ParseConfig(JsonObject? t)
    {
        var s = new Settings();
        if (t == null) return s;
        s.Enabled = Flag(t, "enabled");
        s.Engine = Str(t, "engine", "edge");
        if (s.Engine is not ("edge" or "moss" or "sys")) s.Engine = "edge";
        s.Voice = Str(t, "voice", "");
        s.MossVoice = Str(t, "mossVoice", "Junhao");
        s.Rate = Num(t, "rate", 1);
        s.Pitch = Num(t, "pitch", 1);
        s.Volume = Num(t, "volume", 1);
        s.Gain = Num(t, "gain", 0);
        s.Gap = Num(t, "gap", 0);
        s.QueueMax = (int)Num(t, "queueMax", 8);
        s.GuardPriority = t.TryGetPropertyValue("guardPriority", out var gp) != true
            || (gp is JsonValue gv && gv.TryGetValue<bool>(out var gb) && gb);
        s.BlacklistUids = Str(t, "blacklistUids", "");
        s.BannedWords = Str(t, "bannedWords", "");

        s.Types["danmu"] = ParseType(t, "danmu");
        s.Types["gift"] = ParseType(t, "gift");
        s.Types["superchat"] = ParseType(t, "superchat");
        s.Types["welcome"] = ParseType(t, "welcome");

        if (t.TryGetPropertyValue("tonePresets", out var tp) && tp is JsonObject presets)
        {
            foreach (var (name, node) in presets)
            {
                if (node is not JsonObject p) continue;
                s.Presets[name] = new TonePreset
                {
                    Label = Str(p, "label", ""),
                    Rate = Num(p, "rate", 1),
                    PitchHz = Num(p, "pitch", 0),
                    Voice = Str(p, "voice", ""),
                    Prefix = Str(p, "prefix", ""),
                };
            }
        }
        if (t.TryGetPropertyValue("tone", out var tone) && tone is JsonObject to)
        {
            s.ToneGlobal = Str(to, "global", "normal");
            if (to.TryGetPropertyValue("byType", out var bt) && bt is JsonObject byType)
                foreach (var (k, v) in byType)
                    s.ToneByType[k] = v is JsonValue jv && jv.TryGetValue<string>(out var sv) ? sv ?? "" : "";
        }
        return s;
    }

    private static TypeCfg ParseType(JsonObject t, string key)
    {
        var tc = new TypeCfg();
        if (t.TryGetPropertyValue(key, out var node) && node is JsonObject o)
        {
            tc.Enabled = Flag(o, "enabled");
            tc.SayUid = Flag(o, "sayUid");
            tc.MinMedal = (long)Num(o, "minMedal", 0);
            tc.MinHonor = (long)Num(o, "minHonor", 0);
            tc.Volume = Num(o, "volume", 1);
            if (o.TryGetPropertyValue("texts", out var texts) && texts is JsonArray arr)
                tc.Texts = arr.Where(x => x != null).Select(x => x!.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
        }
        return tc;
    }

    private static bool Flag(JsonObject o, string key, bool def = false)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<bool>(out var b) ? b : def;

    private static string Str(JsonObject o, string key, string def)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<string>(out var s) ? s ?? def : def;

    private static double Num(JsonObject o, string key, double def)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<double>(out var d) ? d : def;

    // ---------------- Chinese numerals (port of numToCn in tts.js) ----------------

    public static string NumToCn(long n)
    {
        if (n <= 0) return "零";
        if (n > 99_999_999) return n.ToString();
        string[] d = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        string[] u = { "", "十", "百", "千" };

        string Section(long x)
        {
            if (x == 0) return "";
            if (x < 10) return d[x];
            if (x < 20) return x == 10 ? "十" : "十" + d[x % 10];
            var sb = new StringBuilder();
            var zero = false;
            for (var i = 3; i >= 0; i--)
            {
                long p = (long)Math.Pow(10, i);
                var dig = x / p % 10;
                if (dig == 0)
                {
                    if (sb.Length > 0 && x % p != 0) zero = true;
                    continue;
                }
                if (zero) { sb.Append('零'); zero = false; }
                sb.Append(d[dig]).Append(u[i]);
            }
            return sb.ToString();
        }

        if (n < 10000) return Section(n);
        var wan = n / 10000;
        var rest = n % 10000;
        var s = Section(wan) + "万";
        if (rest == 0) return s;
        if (rest < 1000) s += "零";
        return s + Section(rest);
    }
}
