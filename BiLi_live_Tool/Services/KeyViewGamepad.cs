using System.Text.Json.Nodes;

namespace BiLi_live_Tool.Services;

#if !WINDOWS
/// <summary>Stub for non-Windows TFMs (KeyView is a desktop-only feature).</summary>
public sealed class KeyViewGamepad
{
    public event Action<string>? OnEventJson;
    public void Start() { }
    public void Stop() { }
}
#else
/// <summary>
/// Port of src/keyview/gamepad-poll.html + input-gamepad.js using WinRT
/// Windows.Gaming.Input.Gamepad (20ms polling, 0.01 diff thresholds) — no
/// hidden window needed and no focus requirement.
/// Event protocol: {t:'gp',i,e:'conn'|'btn'|'trig'|'axis',k,v,ts}.
/// </summary>
public sealed class KeyViewGamepad
{
    public event Action<string>? OnEventJson;

    private static readonly string[] Axes = { "lx", "ly", "rx", "ry" };
    private const double AxisThreshold = 0.01;
    private const double TriggerThreshold = 0.01;
    private const double TriggerPressThreshold = 0.5;

    private CancellationTokenSource? _cts;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var prev = new Dictionary<int, PadState>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var pads = global::Windows.Gaming.Input.Gamepad.Gamepads;
                // Detect removals for indexes that vanished.
                foreach (var kv in prev.ToList())
                {
                    if (kv.Key >= pads.Count && kv.Value.Connected)
                    {
                        prev[kv.Key].Connected = false;
                        Emit(kv.Key, "conn", v: false);
                    }
                }
                for (var i = 0; i < pads.Count; i++)
                {
                    if (!prev.TryGetValue(i, out var st))
                    {
                        st = prev[i] = new PadState();
                    }
                    var reading = pads[i].GetCurrentReading();
                    if (!st.Connected)
                    {
                        st.Connected = true;
                        Emit(i, "conn", v: true, id: "Gamepad");
                    }
                    // Buttons
                    foreach (var (btn, name) in ButtonMap)
                    {
                        var pressed = reading.Buttons.HasFlag(btn);
                        if (pressed == st.Btn[name]) continue;
                        st.Btn[name] = pressed;
                        Emit(i, "btn", name, pressed);
                    }
                    // Triggers (analog + emulated press at 0.5 like the Gamepad API)
                    var lt = reading.LeftTrigger / 255.0;
                    var rt = reading.RightTrigger / 255.0;
                    DiffTrigger(i, st, "lt", lt);
                    DiffTrigger(i, st, "rt", rt);
                    var ltPressed = lt > TriggerPressThreshold;
                    if (ltPressed != st.Btn["LT"]) { st.Btn["LT"] = ltPressed; Emit(i, "btn", "LT", ltPressed); }
                    var rtPressed = rt > TriggerPressThreshold;
                    if (rtPressed != st.Btn["RT"]) { st.Btn["RT"] = rtPressed; Emit(i, "btn", "RT", rtPressed); }
                    // Axes
                    DiffAxis(i, st, 0, "lx", reading.LeftThumbstickX);
                    DiffAxis(i, st, 1, "ly", reading.LeftThumbstickY);
                    DiffAxis(i, st, 2, "rx", reading.RightThumbstickX);
                    DiffAxis(i, st, 3, "ry", reading.RightThumbstickY);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* gamepad polling is optional; keep failures silent */ }
    }

    private static readonly Dictionary<global::Windows.Gaming.Input.GamepadButtons, string> ButtonMap = new()
    {
        [global::Windows.Gaming.Input.GamepadButtons.A] = "A",
        [global::Windows.Gaming.Input.GamepadButtons.B] = "B",
        [global::Windows.Gaming.Input.GamepadButtons.X] = "X",
        [global::Windows.Gaming.Input.GamepadButtons.Y] = "Y",
        [global::Windows.Gaming.Input.GamepadButtons.LeftShoulder] = "LB",
        [global::Windows.Gaming.Input.GamepadButtons.RightShoulder] = "RB",
        [global::Windows.Gaming.Input.GamepadButtons.View] = "Back",
        [global::Windows.Gaming.Input.GamepadButtons.Menu] = "Start",
        [global::Windows.Gaming.Input.GamepadButtons.LeftThumbstick] = "LStick",
        [global::Windows.Gaming.Input.GamepadButtons.RightThumbstick] = "RStick",
        [global::Windows.Gaming.Input.GamepadButtons.DPadUp] = "DUp",
        [global::Windows.Gaming.Input.GamepadButtons.DPadDown] = "DDown",
        [global::Windows.Gaming.Input.GamepadButtons.DPadLeft] = "DLeft",
        [global::Windows.Gaming.Input.GamepadButtons.DPadRight] = "DRight",
    };

    private void DiffTrigger(int i, PadState st, string name, double value)
    {
        var idx = name == "lt" ? 0 : 1;
        if (Math.Abs(value - st.Trig[idx]) > TriggerThreshold)
        {
            st.Trig[idx] = value;
            Emit(i, "trig", name, Math.Round(value * 1000) / 1000);
        }
    }

    private void DiffAxis(int i, PadState st, int idx, string name, double value)
    {
        if (Math.Abs(value - st.Axis[idx]) > AxisThreshold)
        {
            st.Axis[idx] = value;
            Emit(i, "axis", Axes[idx], Math.Round(value * 1000) / 1000);
        }
    }

    private void Emit(int i, string e, string? k = null, object? v = null, string? id = null)
    {
        var node = new JsonObject
        {
            ["t"] = "gp",
            ["i"] = i,
            ["e"] = e,
        };
        if (k != null) node["k"] = k;
        if (v != null)
        {
            if (v is bool b) node["v"] = b;
            else if (v is double d) node["v"] = d;
            else if (v is string s) node["v"] = s;
        }
        if (id != null) node["id"] = id;
        node["ts"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        try { OnEventJson?.Invoke(node.ToJsonString()); } catch { }
    }

    private sealed class PadState
    {
        public bool Connected;
        public Dictionary<string, bool> Btn = new()
        {
            ["A"] = false, ["B"] = false, ["X"] = false, ["Y"] = false,
            ["LB"] = false, ["RB"] = false, ["LT"] = false, ["RT"] = false,
            ["Back"] = false, ["Start"] = false, ["LStick"] = false, ["RStick"] = false,
            ["DUp"] = false, ["DDown"] = false, ["DLeft"] = false, ["DRight"] = false,
        };
        public double[] Axis = new double[4];
        public double[] Trig = new double[2];
    }
}
#endif
