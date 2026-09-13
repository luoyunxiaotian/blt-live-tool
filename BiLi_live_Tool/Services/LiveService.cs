namespace BiLi_live_Tool.Services;

/// <summary>
/// Owns the active danmu connection — thin replacement of connectRoom()
/// in Bin/server.js for the demo scope.
/// </summary>
public sealed class LiveService
{
    private readonly EventHub _hub;
    private DanmuClient? _client;

    public LiveService(EventHub hub) { _hub = hub; }

    public bool IsRunning => _client != null;
    public string CurrentRoomId { get; private set; } = "";

    /// <summary>Raw frames from the live connection (parse diagnostics only).</summary>
    public List<string> RecentRawFrames(string? filter = null)
        => _client?.RecentRawFrames(filter) ?? new List<string>();

    /// <summary>Cmd strings seen on the wire with captured counts.</summary>
    public Dictionary<string, int> SeenCmds()
        => _client?.SeenCmds() ?? new Dictionary<string, int>();

    public void Start(string roomId, string cookie)
    {
        Stop();
        CurrentRoomId = roomId;
        _client = new DanmuClient(_hub);
        _client.Start(roomId, cookie);
    }

    public void Stop()
    {
        _client?.Stop();
        _client = null;
        CurrentRoomId = "";
        if (_hub.LastStatus.State != "idle")
            _hub.PublishStatus(new LiveStatusInfo { State = "idle" });
    }
}
