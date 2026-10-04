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
    private readonly Dictionary<string, long> _lastWelcomeUid = new();
    private readonly Dictionary<string, long> _lastWelcomeName = new();
    private readonly HashSet<string> _followedUids = new();
    private readonly List<string> _followOrder = new();
    private const int FollowCap = 5000;
    private long _demotedAt;
    private string? _lastKnownEngine;
    private bool _speaking;
    private string _currentText = "";
    private readonly Dictionary<string, int> _fails = new();
    private string? _effectiveEngine;
    private volatile bool _skipRequested;

    public void ResetEngineDemotion()
    {
        lock (_lock)
        {
            _effectiveEngine = null;
            _demotedAt = 0;
            _fails.Clear();
        }
    }

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
        public List<string> GuardTexts = new();
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
        lock (_lock)
        {
            _queue.Clear();
            _lastWelcomeUid.Clear();
            _lastWelcomeName.Clear();
        }
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
                // 仅消费按用户聚合汇总后的 gifts_merged 事件，原始 gifts 事件不入队（防连击与多次送礼重复播报，对标原版 tts.js 铁律）
                "gifts_merged" => "gift",
                "superchat" => "superchat",
                // 关注 (msgType=2)
                "interact" when ev.MsgType == 2 => "follow",
                // 仅进入直播间 (msgType=1) 或进场特效 (msgType=0) 触发欢迎语音，过滤点赞、分享等其它互动
                "interact" when (ev.MsgType == 1 || ev.MsgType == 0) => "welcome",
                "guard" => "welcome",
                _ => null,
            };
            if (typeKey == null) return;
            if (!ShouldSpeak(cfg, typeKey, ev)) return;

            if (typeKey == "follow")
            {
                var uidKey = (ev.Uid ?? "").Trim();
                if (uidKey.Length > 0 && uidKey != "0")
                {
                    lock (_lock)
                    {
                        if (_followedUids.Contains(uidKey)) return;
                        _followedUids.Add(uidKey);
                        _followOrder.Add(uidKey);
                        if (_followOrder.Count > FollowCap)
                        {
                            var old = _followOrder[0];
                            _followOrder.RemoveAt(0);
                            _followedUids.Remove(old);
                        }
                    }
                }
            }

            if (typeKey == "welcome")
            {
                var uidKey = (ev.Uid ?? "").Trim();
                var rawName = (ev.Uname ?? "").Trim();
                var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                lock (_lock)
                {
                    // 超过 1000 项时清理过期项，防内存增长
                    if (_lastWelcomeUid.Count > 1000)
                    {
                        var expU = _lastWelcomeUid.Where(kv => now - kv.Value > 120000).Select(kv => kv.Key).ToList();
                        foreach (var k in expU) _lastWelcomeUid.Remove(k);
                    }
                    if (_lastWelcomeName.Count > 1000)
                    {
                        var expN = _lastWelcomeName.Where(kv => now - kv.Value > 120000).Select(kv => kv.Key).ToList();
                        foreach (var k in expN) _lastWelcomeName.Remove(k);
                    }

                    var hasRecentUid = uidKey.Length > 0 && uidKey != "0" && _lastWelcomeUid.TryGetValue(uidKey, out var lastU) && (now - lastU < 60000);
                    var hasRecentName = rawName.Length > 0 && !rawName.StartsWith("用户") && _lastWelcomeName.TryGetValue(rawName, out var lastN) && (now - lastN < 60000);
                    if (hasRecentUid || hasRecentName) return;

                    // 立即占位冷却，拦截伴随的 ENTRY_EFFECT 等高频并发消息
                    if (uidKey.Length > 0 && uidKey != "0") _lastWelcomeUid[uidKey] = now;
                    if (rawName.Length > 0 && !rawName.StartsWith("用户")) _lastWelcomeName[rawName] = now;
                }
            }

            var text = BuildText(cfg, typeKey, ev);
            if (string.IsNullOrWhiteSpace(text)) return;

            if (typeKey == "welcome")
            {
                var resolvedName = ResolveUnameForTts(cfg, typeKey, ev);
                if (!string.IsNullOrWhiteSpace(resolvedName) && !resolvedName.StartsWith("用户") && resolvedName != "观众")
                {
                    var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    lock (_lock)
                    {
                        if (_lastWelcomeName.TryGetValue(resolvedName, out var lastN) && (now - lastN < 60000) && lastN != now)
                        {
                            return;
                        }
                        _lastWelcomeName[resolvedName] = now;
                    }
                }
            }

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

    private static string ResolveUnameForTts(Settings cfg, string typeKey, LiveEvent ev)
    {
        cfg.Types.TryGetValue(typeKey, out var tc);
        if (tc?.SayUid != true) return "";
        var uname = (ev.Uname ?? "").Trim();
        var uid = (ev.Uid ?? "").Trim();
        if (string.IsNullOrWhiteSpace(uname) || uname == "用户" + uid || (uname.StartsWith("用户") && long.TryParse(uname.Substring(2), out _)) || long.TryParse(uname, out _))
        {
            if (!string.IsNullOrWhiteSpace(uid) && BiliApi.UserCache.TryGet(uid, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                uname = cached.Trim();
            }
            else if (long.TryParse(uname, out _))
            {
                uname = "";
            }
        }
        return uname;
    }

    private static string BuildText(Settings cfg, string typeKey, LiveEvent ev)
    {
        cfg.Types.TryGetValue(typeKey, out var tc);
        // 「念昵称」开关（UID 数字永不念，隐私；多语言昵称友好）
        var uname = ResolveUnameForTts(cfg, typeKey, ev);

        switch (typeKey)
        {
            case "danmu":
            {
                var pool = (tc?.Texts != null && tc.Texts.Count > 0)
                    ? tc.Texts
                    : new List<string> { "{uname}说：{msg}", "{uname}：{msg}" };
                var tpl = pool[Random.Shared.Next(pool.Count)];
                return FormatDanmu(tpl, uname, ev.Msg);
            }

            case "gift":
            {
                var list = ev.Gifts ?? new List<GiftItem>();
                if (list.Count == 0 && !string.IsNullOrWhiteSpace(ev.GiftName))
                {
                    list = new List<GiftItem> { new GiftItem(ev.GiftName, Math.Max(1, ev.Num), ev.Price) };
                }

                // 礼物汇总（如"粉丝团灯牌一百个"或"小花花五个、牛哇牛哇七个"；数量为1时省略数量更自然）
                var parts = list.Select(g =>
                {
                    var n = Math.Max(1, g.Num);
                    var name = string.IsNullOrWhiteSpace(g.GiftName) ? "礼物" : g.GiftName.Trim();
                    return name + (n > 1 ? NumToCn(n) + "个" : "");
                }).Where(s => s.Length > 0).ToList();

                var summary = parts.Count > 0 ? string.Join("、", parts) : "礼物";

                var single = list.Count == 1 ? list[0] : null;
                var gname = single != null
                    ? (string.IsNullOrWhiteSpace(single.GiftName) ? "礼物" : single.GiftName.Trim())
                    : summary;
                var num = single != null ? Math.Max(1, single.Num) : (int)Math.Max(1, ev.TotalNum > 0 ? ev.TotalNum : list.Sum(g => g.Num));
                var numCn = NumToCn(num);

                var pool = (tc?.Texts != null && tc.Texts.Count > 0)
                    ? tc.Texts
                    : new List<string>
                    {
                        "感谢{uname}送出的{giftSummary}",
                        "感谢{uname}送出的{num}个{giftName}",
                        "多谢{uname}老板送出的{giftSummary}",
                        "谢谢{uname}投喂的{giftSummary}"
                    };
                var tpl = pool[Random.Shared.Next(pool.Count)];
                return FormatGift(tpl, uname, gname, num, numCn, summary);
            }

            case "superchat":
            {
                var priceStr = ev.Price > 0 ? ev.Price.ToString() : "";
                var pool = (tc?.Texts != null && tc.Texts.Count > 0)
                    ? tc.Texts
                    : new List<string>
                    {
                        "醒目留言，{uname}说：{msg}",
                        "收到一条醒目留言，来自{uname}：{msg}"
                    };
                var tpl = pool[Random.Shared.Next(pool.Count)];
                return FormatSc(tpl, uname, ev.Msg, priceStr);
            }

            case "welcome":
            {
                var isGuard = ev.IsGuard || ev.GuardLevel >= 1 || ev.Type == "guard";
                var guardName = ev.GuardLevel switch
                {
                    1 => "总督",
                    2 => "提督",
                    3 => "舰长",
                    _ => isGuard ? "舰长" : ""
                };

                if (isGuard && tc?.GuardTexts != null && tc.GuardTexts.Count > 0)
                {
                    var gtpl = tc.GuardTexts[Random.Shared.Next(tc.GuardTexts.Count)];
                    return FormatWelcome(gtpl, uname, guardName);
                }

                var pool = (tc?.Texts != null && tc.Texts.Count > 0)
                    ? tc.Texts
                    : new List<string> { "欢迎 {uname} 进入直播间", "{uname} 来啦，欢迎欢迎", "欢迎 {uname} 的到来" };
                var tpl = pool[Random.Shared.Next(pool.Count)];
                return FormatWelcome(tpl, uname, guardName);
            }

            case "follow":
            {
                var pool = (tc?.Texts != null && tc.Texts.Count > 0)
                    ? tc.Texts
                    : new List<string> { "感谢 {uname} 的关注", "多谢 {uname} 点的关注", "感谢 {uname} 关注主播" };
                var tpl = pool[Random.Shared.Next(pool.Count)];
                return FormatFollow(tpl, uname);
            }
        }
        return "";
    }

    private static string FormatDanmu(string tpl, string uname, string msg)
    {
        var res = tpl;
        if (uname.Length > 0)
        {
            res = res.Replace("{uname}", uname);
        }
        else
        {
            res = res.Replace("{uname}说：", "说：")
                     .Replace("{uname}说:", "说:")
                     .Replace("{uname}：", "")
                     .Replace("{uname}:", "")
                     .Replace("{uname}", "");
        }
        res = res.Replace("{msg}", msg);
        return res.Trim();
    }

    private static string FormatGift(string tpl, string uname, string giftName, int num, string numCn, string summary)
    {
        var res = tpl;
        if (uname.Length > 0)
        {
            res = res.Replace("{uname}", uname);
        }
        else
        {
            res = res.Replace("感谢{uname}", "感谢")
                     .Replace("谢谢{uname}", "谢谢")
                     .Replace("多谢{uname}", "多谢")
                     .Replace("{uname}", "");
        }

        // 防呆保护：若模板中既未写 {num}，也未写 {count}，也未写 {giftSummary}
        // 且送出的礼物数量大于 1（或为多礼物汇总），则 {giftName} 自动带上数量后缀（如"粉丝团灯牌一百个"），绝不丢失数量！
        var effectiveGiftName = giftName;
        if (!res.Contains("{num}") && !res.Contains("{count}") && !res.Contains("{giftSummary}") && num > 1)
        {
            effectiveGiftName = summary;
        }

        // 占位符替换：
        // 1. 若模板写了 "{num}个"，由于 numCn 是 "一百"，合并处理为 "一百个"（当 num=1 时为 "一个"），杜绝 "一百个个" 叠字语病
        if (res.Contains("{num}个"))
        {
            res = res.Replace("{num}个", num > 1 ? numCn + "个" : "一个");
        }
        else
        {
            res = res.Replace("{num}", numCn);
        }

        res = res.Replace("{giftSummary}", summary)
                 .Replace("{giftName}", effectiveGiftName)
                 .Replace("{count}", num.ToString());

        return res.Trim();
    }

    private static string FormatSc(string tpl, string uname, string msg, string price)
    {
        var res = tpl;
        if (uname.Length > 0)
        {
            res = res.Replace("{uname}", uname);
        }
        else
        {
            res = res.Replace("{uname}说：", "说：")
                     .Replace("{uname}说:", "说:")
                     .Replace("{uname}：", "")
                     .Replace("{uname}:", "")
                     .Replace("来自{uname}：", "")
                     .Replace("来自{uname}:", "")
                     .Replace("{uname}", "");
        }
        res = res.Replace("{msg}", msg)
                 .Replace("{price}", price.Length > 0 ? price + "元" : "");
        return res.Trim();
    }

    private static string FormatWelcome(string tpl, string uname, string guardName)
    {
        var name = uname.Length > 0 ? uname : "观众";
        return tpl.Replace("{uname}", name)
                  .Replace("{guardName}", guardName.Length > 0 ? guardName : "舰长")
                  .Trim();
    }

    private static string FormatFollow(string tpl, string uname)
    {
        var res = tpl;
        if (uname.Length > 0)
        {
            res = res.Replace("{uname}", uname);
        }
        else
        {
            res = res.Replace("感谢 {uname} 的关注", "感谢关注")
                     .Replace("感谢{uname}的关注", "感谢关注")
                     .Replace("多谢 {uname} 点的关注", "多谢点关注")
                     .Replace("多谢{uname}点的关注", "多谢点关注")
                     .Replace("感谢 {uname} 关注主播", "感谢关注主播")
                     .Replace("感谢{uname}关注主播", "感谢关注主播")
                     .Replace("感谢{uname}", "感谢")
                     .Replace("多谢{uname}", "多谢")
                     .Replace("{uname}", "");
        }
        return res.Trim();
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

            var now = Environment.TickCount64;
            // 自动探活与恢复机制：
            // 如果由于此前偶发网络抖动导致临时降级，在冷却期（30 秒）后自动尝试恢复主播配置的首选引擎！
            if (_effectiveEngine != null && _effectiveEngine != cfg.Engine)
            {
                if (now - _demotedAt >= 30_000)
                {
                    _effectiveEngine = null;
                    _fails.Clear();
                }
            }

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
                    _demotedAt = Environment.TickCount64;
                    LastResult = $"demote {engine}->{next}";
                    ServiceLog.Warn("语音", $"{engine} TTS 暂时不可用，临时降级至 {next} 语音，30秒后将自动尝试恢复...");
                    // 本条立即降级重试一次
                    ok = await TryEngineAsync(next, text, voice, cfg, rate, pitchHz, volume);
                }
            }
            else
            {
                _fails[engine] = 0;
                LastResult = "ok:" + engine;
                if (engine == cfg.Engine)
                {
                    if (_demotedAt > 0)
                    {
                        ServiceLog.Info("语音", $"{cfg.Engine} TTS 已自动恢复正常。");
                        _demotedAt = 0;
                    }
                    _effectiveEngine = null;
                }
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
                    // 系统语音：pitch 是倍数（1 ± Hz/50）。音色沿用 tts.voice —— 选的是
                    // 系统音色就按名字匹配，选的是 edge/moss 音色则匹配不上、自动退回中文音色。
                    return await _audio.SpeakSystemAsync(text, rate, Math.Clamp(1 + pitchHz / 50.0, 0.5, 2), Math.Min(1, volume),
                        voice.Length > 0 ? voice : cfg.Voice);

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
                    if (bytes == null || bytes.Length < 100)
                    {
                        // 快速重试一次：可能 edge 服务瞬时连接重置或正在自愈拉起
                        await Task.Delay(300);
                        bytes = await PostSynthAsync("edge", body);
                    }
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
        if (_lastKnownEngine != null && _lastKnownEngine != _cfg.Engine)
        {
            ResetEngineDemotion();
        }
        _lastKnownEngine = _cfg.Engine;
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
        s.Types["follow"] = ParseType(t, "follow");

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
            if (o.TryGetPropertyValue("guardTexts", out var guardTexts) && guardTexts is JsonArray garr)
                tc.GuardTexts = garr.Where(x => x != null).Select(x => x!.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
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
