using Microsoft.Extensions.DependencyInjection;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Port of src/keyview/server.js orchestration on top of the embedded Kestrel:
/// start/stop of the global hooks + gamepad poller, config store, and the
/// event frames that KestrelHost broadcasts to the KeyView overlay WebSocket
/// clients (root-path /ws on port 7460 — see the root WebSocket branch there).
/// </summary>
public sealed class KeyViewService
{
    private readonly KeyViewConfigStore _config = new();
    private readonly KeyViewHook _hook = new();
    private readonly KeyViewGamepad _gamepad = new();
    private readonly object _lock = new();
    private bool _running;

    /// <summary>Input event frames (already serialized JSON) for overlay clients.</summary>
    public event Action<string>? OnEventJson;

    /// <summary>{t:'cfg',...} frames when the config changes.</summary>
    public event Action<string>? OnConfigFrame;

    /// <summary>Live overlay client count, wired by KestrelHost.</summary>
    public Func<int>? ClientCount;

    public KeyViewConfigStore Config => _config;

    /// <summary>钩子重挂次数（诊断）。</summary>
    public int HookReinstalls => _hook.Reinstalls;

    public bool Running
    {
        get { lock (_lock) return _running; }
    }

    public KeyViewService()
    {
        _hook.OnEventJson += json => OnEventJson?.Invoke(json);
        _gamepad.OnEventJson += json => OnEventJson?.Invoke(json);
        _config.Changed += key =>
        {
            try
            {
                var node = new System.Text.Json.Nodes.JsonObject { ["t"] = "cfg" };
                if (key == "*") node["full"] = _config.GetAll();
                else
                {
                    node["key"] = key;
                    node["v"] = GetConfigValue(key);
                }
                OnConfigFrame?.Invoke(node.ToJsonString());
            }
            catch { }
        };
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
        }
        _hook.Start();
        _gamepad.Start();
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
        }
        // Best-effort stop: a failure in one capture source must not surface
        // as an endpoint error — the state flag above is the source of truth.
        try { _hook.Stop(); } catch { }
        try { _gamepad.Stop(); } catch { }
    }

    /// <summary>手动重挂输入钩子（诊断；看护会自动做这件事）。</summary>
    public void ReinstallHooks() => _hook.Reinstall();

    public object Status()
    {
        lock (_lock)
            return new
            {
                running = _running,
                clients = ClientCount?.Invoke() ?? 0,
                // 诊断：按键时 events 在涨、lastEventAgoMs 很小 → 钩子活着（与前台窗口无关）；
                // reinstalls > 0 说明钩子曾被系统摘掉、已被看护重挂
                events = _hook.Events,
                lastEventAgoMs = _hook.LastEventAgoMs,
                reinstalls = _hook.Reinstalls,
                overlayUrl = $"http://127.0.0.1:{(MauiProgram.Services?.GetService<AppConfig>()?.Port ?? 7460)}/keyview/overlay.html",
            };
    }

    private System.Text.Json.Nodes.JsonNode? GetConfigValue(string key)
        => _config.GetAll().TryGetPropertyValue(key, out var v) ? v : null;
}
