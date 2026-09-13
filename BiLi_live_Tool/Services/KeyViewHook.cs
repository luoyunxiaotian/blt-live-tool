using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace BiLi_live_Tool.Services;

#if !WINDOWS
/// <summary>Stub for non-Windows TFMs (KeyView is a desktop-only feature).</summary>
public sealed class KeyViewHook
{
    public event Action<string>? OnEventJson;
    public void Start() { }
    public void Stop() { }
}
#else
/// <summary>
/// Port of src/keyview/input-keyboard-mouse.js on Win32 low-level hooks
/// (WH_KEYBOARD_LL / WH_MOUSE_LL) instead of uiohook-napi.
/// Read-only: always calls CallNextHookEx (never swallows input).
/// The hooks are installed on a dedicated thread that pumps messages; events
/// are pushed into a channel so the hook callback never blocks (a slow
/// callback would get the hook silently removed by the system).
/// </summary>
public sealed class KeyViewHook
{
    /// <summary>Serialized {t:'kb'|'ms',...} frames, matching the original protocol.</summary>
    public event Action<string>? OnEventJson;

    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const uint WmQuit = 0x0012;

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;

    private const int WmLbuttonDown = 0x0201, WmLbuttonUp = 0x0202;
    private const int WmRbuttonDown = 0x0204, WmRbuttonUp = 0x0205;
    private const int WmMbuttonDown = 0x0207, WmMbuttonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmXbuttonDown = 0x020B, WmXbuttonUp = 0x020C;
    private const int WmMouseHWheel = 0x020D;
    private const int WmMouseMove = 0x0200;

    private static readonly Dictionary<int, string> VkNames = new()
    {
        [0x01] = "LMB", [0x02] = "RMB", [0x04] = "MMB", [0x05] = "X1", [0x06] = "X2",
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x10] = "Shift", [0x11] = "Ctrl", [0x12] = "Alt",
        [0x13] = "Pause", [0x14] = "CapsLock", [0x15] = "Kana", [0x1B] = "Esc", [0x20] = "Space",
        [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete",
        [0x30] = "0", [0x31] = "1", [0x32] = "2", [0x33] = "3", [0x34] = "4", [0x35] = "5", [0x36] = "6", [0x37] = "7", [0x38] = "8", [0x39] = "9",
        [0x41] = "A", [0x42] = "B", [0x43] = "C", [0x44] = "D", [0x45] = "E", [0x46] = "F", [0x47] = "G", [0x48] = "H", [0x49] = "I", [0x4A] = "J",
        [0x4B] = "K", [0x4C] = "L", [0x4D] = "M", [0x4E] = "N", [0x4F] = "O", [0x50] = "P", [0x51] = "Q", [0x52] = "R", [0x53] = "S", [0x54] = "T",
        [0x55] = "U", [0x56] = "V", [0x57] = "W", [0x58] = "X", [0x59] = "Y", [0x5A] = "Z",
        [0x5B] = "Win", [0x5C] = "Win", [0x5D] = "Menu",
        [0x60] = "Num0", [0x61] = "Num1", [0x62] = "Num2", [0x63] = "Num3", [0x64] = "Num4",
        [0x65] = "Num5", [0x66] = "Num6", [0x67] = "Num7", [0x68] = "Num8", [0x69] = "Num9",
        [0x6A] = "Num*", [0x6B] = "Num+", [0x6D] = "Num-", [0x6E] = "Num.", [0x6F] = "Num/",
        [0x70] = "F1", [0x71] = "F2", [0x72] = "F3", [0x73] = "F4", [0x74] = "F5", [0x75] = "F6",
        [0x76] = "F7", [0x77] = "F8", [0x78] = "F9", [0x79] = "F10", [0x7A] = "F11", [0x7B] = "F12",
        [0x90] = "NumLock", [0x91] = "ScrollLock",
        [0xA0] = "LShift", [0xA1] = "RShift", [0xA2] = "LCtrl", [0xA3] = "RCtrl", [0xA4] = "LAlt", [0xA5] = "RAlt",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/",
        [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
        [0xE2] = "OEM102",
    };

    private static string KeyName(int vk) => VkNames.TryGetValue(vk, out var n) ? n : "K" + vk;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _kbHook;
    private IntPtr _msHook;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    // The hook proc is static (native raw pointer), so it routes to whichever
    // instance started last — there is only ever one active hook set.
    private static KeyViewHook? _active;

    // Keep the hook proc delegate alive for the process lifetime (native code
    // holds the raw pointer).
    private static readonly LowLevelProc HookProcDelegate = HookProc;

    public void Start()
    {
        if (_thread != null) return;
        _active = this;
        _thread = new Thread(Run) { IsBackground = true, Name = "KeyViewHook" };
        _thread.Start();
        _ = Task.Run(DrainAsync);
    }

    public void Stop()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
        _thread = null;
        _threadId = 0;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _kbHook = SetWindowsHookExW(WhKeyboardLl, HookProcDelegate, GetModuleHandleW(null), 0);
        _msHook = SetWindowsHookExW(WhMouseLl, HookProcDelegate, GetModuleHandleW(null), 0);
        // Message pump keeps the LL hooks alive on this thread.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref msg);
            _ = DispatchMessage(ref msg);
        }
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);
        _kbHook = _msHook = IntPtr.Zero;
    }

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var inst = _active;
        if (nCode >= 0 && inst != null)
        {
            try { inst.Handle((uint)wParam, lParam); }
            catch { /* never let the hook throw */ }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private long _lastMoveTicks;

    private void Handle(uint msg, IntPtr lParam)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        switch (msg)
        {
            case WmKeyDown or WmSysKeyDown:
            {
                var s = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                EmitKb("down", (int)s.vkCode, now);
                break;
            }
            case WmKeyUp or WmSysKeyUp:
            {
                var s = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                EmitKb("up", (int)s.vkCode, now);
                break;
            }
            case WmLbuttonDown or WmLbuttonUp:
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                EmitMs(msg == WmLbuttonDown ? "down" : "up", "left", s.pt.x, s.pt.y, now);
                break;
            }
            case WmRbuttonDown or WmRbuttonUp:
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                EmitMs(msg == WmRbuttonDown ? "down" : "up", "right", s.pt.x, s.pt.y, now);
                break;
            }
            case WmMbuttonDown or WmMbuttonUp:
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                EmitMs(msg == WmMbuttonDown ? "down" : "up", "middle", s.pt.x, s.pt.y, now);
                break;
            }
            case WmXbuttonDown or WmXbuttonUp:
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                // X1/X2 live in the high word of mouseData.
                var xb = (short)((s.mouseData >> 16) & 0xFFFF);
                EmitMs(msg == WmXbuttonDown ? "down" : "up", xb == 2 ? "x2" : "x1", s.pt.x, s.pt.y, now);
                break;
            }
            case WmMouseWheel or WmMouseHWheel:
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var delta = (short)((s.mouseData >> 16) & 0xFFFF);
                int dx = 0, dy = 0;
                if (msg == WmMouseHWheel) dx = delta / 120;
                else dy = delta / 120;
                Emit($"{{\"t\":\"ms\",\"e\":\"wheel\",\"dx\":{dx},\"dy\":{dy},\"x\":{s.pt.x},\"y\":{s.pt.y},\"ts\":{now}}}");
                break;
            }
            case WmMouseMove:
            {
                var t = Environment.TickCount64;
                if (t - _lastMoveTicks < 16) return;
                _lastMoveTicks = t;
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                Emit($"{{\"t\":\"ms\",\"e\":\"move\",\"x\":{s.pt.x},\"y\":{s.pt.y},\"ts\":{now}}}");
                break;
            }
        }
    }

    private void EmitKb(string e, int vk, long ts)
        => Emit($"{{\"t\":\"kb\",\"e\":\"{e}\",\"k\":\"{KeyName(vk)}\",\"code\":{vk},\"ts\":{ts}}}");

    private void EmitMs(string e, string button, int x, int y, long ts)
        => Emit($"{{\"t\":\"ms\",\"e\":\"{e}\",\"b\":\"{button}\",\"x\":{x},\"y\":{y},\"ts\":{ts}}}");

    private void Emit(string json) => _queue.Writer.TryWrite(json);

    private async Task DrainAsync()
    {
        await foreach (var json in _queue.Reader.ReadAllAsync())
        {
            try { OnEventJson?.Invoke(json); }
            catch { }
        }
    }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
}
#endif
