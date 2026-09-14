using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BiLi_live_Tool.Services;

public sealed record GiftItem(string GiftName, int Num, long Price);

/// <summary>Normalized live event (port of normalize() output in Bin/lib/bili.js).</summary>
public sealed class LiveEvent
{
    public string Type { get; set; } = "";       // danmu|gifts|guard|superchat|interact|pk|gifts_merged|blindbox
    public string Time { get; set; } = "";
    public long Ts { get; set; }
    public string Uid { get; set; } = "";
    public string Uname { get; set; } = "";
    public string Msg { get; set; } = "";        // danmu text / SC message
    public string Uface { get; set; } = "";
    public string GiftName { get; set; } = "";
    public int Num { get; set; } = 1;
    public long Price { get; set; }
    public long TotalCoin { get; set; }

    /// <summary>ONLINE_RANK_V2/V3 payload: the current audience leaderboard (\u5728\u7ebf\u699c).</summary>
    public List<OnlineRankEntry>? Rank { get; set; }
    public string CoinType { get; set; } = "gold";
    public double Value { get; set; }            // CNY estimate
    public int Level { get; set; }               // guard level 1/2/3
    public string LevelName { get; set; } = "";
    public int MedalLevel { get; set; }
    public int HonorLevel { get; set; }
    public bool IsGuard { get; set; }
    public int GuardLevel { get; set; }
    public long StartTime { get; set; }          // superchat
    public long EndTime { get; set; }            // superchat
    public int MsgType { get; set; }             // interact: 1=enter, 2=follow
    // blindbox
    public double Cost { get; set; }
    public double Income { get; set; }
    public double Profit { get; set; }
    // gifts_merged (GiftAggregator output)
    public long TotalNum { get; set; }
    public List<GiftItem>? Gifts { get; set; }
    // pk (raw cmd + payload kept for the tracker)
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cmd { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Data { get; set; }
}

/// <summary>Connection status snapshot — field names match status in Bin/server.js:178.</summary>
public sealed class LiveStatusInfo
{
    public string State { get; set; } = "idle"; // idle|resolving|checking_live|not_live|getting_danmu|connected|reconnecting|disconnected|error
    public string RealRoomId { get; set; } = "";
    public string AnchorUid { get; set; } = "";
    public long Popularity { get; set; }
    public long ConnectedAt { get; set; }
    public string Error { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>
/// Thread-safe event bus. The Razor page subscribes for UI updates, the embedded
/// Kestrel server broadcasts {type, data} frames to /ws clients and serves
/// /api/recent?type= from the per-type ring buffers (original: 300 per type).
/// </summary>
public sealed class EventHub
{
    private const int RecentCap = 300;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<LiveEvent>> _recent = new();

    public event Action<LiveEvent>? OnEvent;
    public event Action<LiveStatusInfo>? StatusChanged;
    /// <summary>Outbound non-event WS frames: song_request / pkinfo / alert_test / widgets / monitor.</summary>
    public event Action<string, object?>? OnOutbound;

    public LiveStatusInfo LastStatus { get; private set; } = new();

    public void PublishOutbound(string type, object? data) => OnOutbound?.Invoke(type, data);

    public void Publish(LiveEvent ev)
    {
        lock (_lock)
        {
            if (!_recent.TryGetValue(ev.Type, out var list))
                _recent[ev.Type] = list = new List<LiveEvent>();
            list.Add(ev);
            if (list.Count > RecentCap) list.RemoveRange(0, list.Count - RecentCap);
        }
        OnEvent?.Invoke(ev);
    }

    public List<LiveEvent> GetRecent(string type)
    {
        lock (_lock)
            return _recent.TryGetValue(type, out var list) ? new List<LiveEvent>(list) : new List<LiveEvent>();
    }

    public void PublishStatus(LiveStatusInfo st)
    {
        LastStatus = st;
        StatusChanged?.Invoke(st);
    }
}

/// <summary>One 在线榜 entry (uid/uname/score/rank) as pushed by ONLINE_RANK_V2/V3.</summary>
public sealed record OnlineRankEntry(long Uid, string Uname, string Score, int Rank);
