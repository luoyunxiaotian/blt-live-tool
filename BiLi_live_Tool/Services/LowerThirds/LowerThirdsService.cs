using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiLi_live_Tool.Services.SystemMedia;

namespace BiLi_live_Tool.Services.LowerThirds;

public class LowerThirdsService : IDisposable
{
    private readonly AppConfig _config;
    private readonly SystemMediaService _mediaService;
    private readonly EventHub _hub;
    private readonly object _lock = new();
    private readonly Dictionary<int, CancellationTokenSource> _dismissTokens = new();

    private LowerThirdConfig _data = new();
    private string _configPath = "";

    public LowerThirdConfig Data
    {
        get
        {
            lock (_lock) return _data;
        }
    }

    public event Action<LowerThirdChannel>? ChannelChanged;
    public event Action<string, object>? OverlayBroadcastNeeded; // (event, payload)

    public LowerThirdsService(AppConfig config, SystemMediaService mediaService, EventHub hub)
    {
        _config = config;
        _mediaService = mediaService;
        _hub = hub;
        InitConfig();

        // 监听外部媒体切歌，实现切歌自动气泡联动
        _mediaService.TrackChanged += OnExternalTrackChanged;
        OverlayBroadcastNeeded += (evt, data) => _hub.PublishOutbound(evt, data);
    }

    private void InitConfig()
    {
        try
        {
            _configPath = Path.Combine(AppConfig.DataDir, "lower-thirds-config.json");
            if (File.Exists(_configPath))
            {
                string json = File.ReadAllText(_configPath);
                var loaded = JsonSerializer.Deserialize<LowerThirdConfig>(json);
                if (loaded != null && loaded.Channels.Count > 0)
                {
                    _data = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LowerThirds] Load error: {ex.Message}");
        }

        if (_data.Channels.Count == 0)
        {
            _data = new LowerThirdConfig
            {
                Enabled = true,
                AutoMusicBubble = true,
                MusicBubbleDuration = 6.0,
                AvoidConflict = true,
                Channels = new List<LowerThirdChannel>
                {
                    LowerThirdChannel.CreateDefault(1, "主播 / 主持人", "#FB7299", "#E0567C"),
                    LowerThirdChannel.CreateDefault(2, "特邀嘉宾", "#00A1D6", "#0080B0"),
                    LowerThirdChannel.CreateDefault(3, "实时议题 / 提示", "#22C55E", "#16A34A"),
                    LowerThirdChannel.CreateDefault(4, "社交媒体 / 动态", "#F59E0B", "#D97706")
                }
            };
            Save();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                string dir = Path.GetDirectoryName(_configPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LowerThirds] Save error: {ex.Message}");
            }
        }
    }

    public LowerThirdChannel? GetChannel(int channelId)
    {
        lock (_lock)
        {
            return _data.Channels.Find(c => c.Id == channelId);
        }
    }

    public void ToggleChannel(int channelId, bool? setActive = null)
    {
        LowerThirdChannel? ch;
        lock (_lock)
        {
            ch = _data.Channels.Find(c => c.Id == channelId);
            if (ch == null) return;

            bool target = setActive ?? !ch.Active;
            ch.Active = target;
        }

        CancelDismissTimer(channelId);

        if (ch.Active)
        {
            // 防遮挡冲突检测：若开启避让，广播避让信号
            if (_data.AvoidConflict && channelId == 1)
            {
                OverlayBroadcastNeeded?.Invoke("overlay_avoid", new { action = "COLLAPSE_MUSIC", reason = "LowerThirdActive" });
            }

            // 单次播放自动退场
            if (ch.OneShot && !ch.LockActive && ch.ActiveTime > 0)
            {
                var cts = new CancellationTokenSource();
                _dismissTokens[channelId] = cts;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        int delayMs = (int)(ch.ActiveTime * 1000);
                        await Task.Delay(delayMs, cts.Token);
                        ToggleChannel(channelId, false);
                    }
                    catch (TaskCanceledException) { }
                });
            }
        }

        BroadcastChannelState(ch);
        ChannelChanged?.Invoke(ch);
    }

    public void UpdateChannel(int channelId, Action<LowerThirdChannel> updateAction, bool broadcast = true)
    {
        LowerThirdChannel? ch;
        lock (_lock)
        {
            ch = _data.Channels.Find(c => c.Id == channelId);
            if (ch == null) return;
            updateAction(ch);
        }

        Save();

        if (broadcast && ch.Active)
        {
            BroadcastChannelState(ch);
        }
        ChannelChanged?.Invoke(ch);
    }

    public void SetGlobalConfig(bool? enabled = null, bool? autoMusicBubble = null, double? musicBubbleDuration = null, bool? avoidConflict = null)
    {
        lock (_lock)
        {
            if (enabled.HasValue) _data.Enabled = enabled.Value;
            if (autoMusicBubble.HasValue) _data.AutoMusicBubble = autoMusicBubble.Value;
            if (musicBubbleDuration.HasValue) _data.MusicBubbleDuration = musicBubbleDuration.Value;
            if (avoidConflict.HasValue) _data.AvoidConflict = avoidConflict.Value;
        }
        Save();
    }

    public void ApplySlot(int channelId, int slotId, bool activate = true)
    {
        LowerThirdChannel? ch;
        lock (_lock)
        {
            ch = _data.Channels.Find(c => c.Id == channelId);
            if (ch == null) return;

            var slot = ch.Slots.Find(s => s.Id == slotId);
            if (slot == null) return;

            ch.CurrentSlotId = slotId;
            ch.Name = slot.Name;
            ch.Info = slot.Info;
            ch.Style = slot.Style;
            ch.Align = slot.Align;
            ch.Color1 = slot.Color1;
            ch.Color2 = slot.Color2;
            ch.TextColor1 = slot.TextColor1;
            ch.TextColor2 = slot.TextColor2;
            ch.Logo = slot.Logo;
            ch.ShowLogo = slot.ShowLogo;
        }

        Save();

        if (activate)
        {
            ToggleChannel(channelId, true);
        }
        else
        {
            BroadcastChannelState(ch);
            ChannelChanged?.Invoke(ch);
        }
    }

    public void SaveCurrentAsSlot(int channelId, int slotId, string? label = null)
    {
        lock (_lock)
        {
            var ch = _data.Channels.Find(c => c.Id == channelId);
            if (ch == null) return;

            var slot = ch.Slots.Find(s => s.Id == slotId);
            if (slot == null) return;

            if (!string.IsNullOrWhiteSpace(label)) slot.Label = label;
            slot.Name = ch.Name;
            slot.Info = ch.Info;
            slot.Style = ch.Style;
            slot.Align = ch.Align;
            slot.Color1 = ch.Color1;
            slot.Color2 = ch.Color2;
            slot.TextColor1 = ch.TextColor1;
            slot.TextColor2 = ch.TextColor2;
            slot.Logo = ch.Logo;
            slot.ShowLogo = ch.ShowLogo;
        }

        Save();
    }

    private void OnExternalTrackChanged(SystemMediaTrack track)
    {
        if (!_data.AutoMusicBubble || !track.HasSong) return;

        // 构造临时 Lower Third 音乐气泡广播
        OverlayBroadcastNeeded?.Invoke("music_bubble", new
        {
            action = "SHOW",
            title = track.Title,
            artist = track.Artist,
            album = track.Album,
            source = track.SourceApp,
            coverUrl = track.CoverUrl,
            duration = _data.MusicBubbleDuration
        });
    }

    public void TriggerTestMusicBubble(string title = "测试歌曲 - Live Special", string artist = "音乐人", string album = "单曲精选", string source = "NetEaseMusic", string coverUrl = "logos/logo_3.png")
    {
        OverlayBroadcastNeeded?.Invoke("music_bubble", new
        {
            action = "SHOW",
            title,
            artist,
            album,
            source,
            coverUrl,
            duration = _data.MusicBubbleDuration
        });
    }

    private void CancelDismissTimer(int channelId)
    {
        if (_dismissTokens.TryGetValue(channelId, out var oldCts))
        {
            oldCts.Cancel();
            _dismissTokens.Remove(channelId);
        }
    }

    public void BroadcastChannelState(LowerThirdChannel ch)
    {
        OverlayBroadcastNeeded?.Invoke("lower_third", ch);
    }

    public void Dispose()
    {
        _mediaService.TrackChanged -= OnExternalTrackChanged;
        foreach (var cts in _dismissTokens.Values)
        {
            cts.Cancel();
        }
        _dismissTokens.Clear();
    }
}
