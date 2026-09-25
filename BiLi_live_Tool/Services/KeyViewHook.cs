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
    public long Events => 0;
    public long LastEventAgoMs => -1;
    public int Reinstalls => 0;
    public long HookHits => 0;
    public long RawHits => 0;
    public long HookKbHits => 0;
    public long RawKbHits => 0;
    public long PollHits => 0;
    public long PollKbHits => 0;
    public long HookCalls => 0;
    public bool HooksInstalled => false;
    public string HooksDetail => "n/a";
    public void Reinstall() { }
}
#else
/// <summary>
/// Port of src/keyview/input-keyboard-mouse.js on Win32 low-level hooks
/// (WH_KEYBOARD_LL / WH_MOUSE_LL) instead of uiohook-napi.
/// Read-only: always calls CallNextHookEx (never swallows input).
/// The hooks are installed on a dedicated thread that pumps messages; events
/// are pushed into a channel so the hook callback never blocks (a slow
/// callback would get the hook silently removed by the system).
///
/// Windows really does remove low-level hooks silently (callback over
/// LowLevelHooksTimeout, session transitions, driver churn) and never puts them
/// back — the overlay then looks dead until the app is restarted, which is the
/// "偶尔不再响应键鼠" report. A watchdog compares the system's own
/// "last real input" stamp (GetLastInputInfo, independent of this process) with
/// the last event we received and re-installs the hooks on our hook thread when
/// input exists that we did not see.
/// </summary>
public sealed class KeyViewHook
{
    /// <summary>Serialized {t:'kb'|'ms',...} frames, matching the original protocol.</summary>
    public event Action<string>? OnEventJson;

    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const uint WmQuit = 0x0012;
    private const uint WmReinstall = 0x8000 + 0x51;   // WM_APP+81：请钩子线程重挂（自定义消息）
    private const uint WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const int RimTypeMouse = 0;
    private const int RimTypeKeyboard = 1;
    private static readonly IntPtr HwndMessage = new(-3);

    // 存活看护参数：系统说"刚刚有人动过键鼠"，而我们这段时间一个事件都没收到 → 钩子被摘了
    private const int WatchIntervalMs = 5000;
    private const int InputFreshMs = 1500;      // 系统输入时间戳多久内算"刚刚"
    private const int MissGraceMs = 3000;       // 我们多久没收到事件才认定被摘

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

    private Timer? _watch;
    private long _lastEventTicks;
    private long _events;
    private int _reinstalls;

    // ---- Raw Input（第二路采集）----
    // 低级钩子会被挡在门外（前台窗口完整性更高、游戏/反外挂的输入链），而 Raw Input 的
    // RIDEV_INPUTSINK 正是为这种场景准备的：系统把 WM_INPUT 投给**我们自己的**消息窗口，
    // 不做任何跨进程注入，所以前台是游戏（乃至更高完整性）时依然收得到。
    private IntPtr _rawWnd;
    private IntPtr _oldWndProc;
    private WndProc? _wndProcKeepAlive;
    private Thread? _rawThread;
    private uint _rawThreadId;
    private long _hookHits;
    private long _rawHits;
    private long _hookKbHits;
    private long _rawKbHits;
    private long _hookCalls;             // 钩子回调被调用的次数（0 = 系统根本没回调我们）
    private long _lastHookHitTicks;      // 钩子这一路最后一次收到事件的时间（按路看护用）
    private long _lastOtherHitTicks;     // 其它两路最后一次收到事件的时间

    // ---- 轮询兜底（第三路）----
    // GetAsyncKeyState 读的是内核维护的"物理按键状态"（钩子链之前就已确定），所以哪怕某游戏
    // 把低级钩子和 Raw Input 都挡了，这一路照样能看出按键的按下/抬起；鼠标位置用 GetCursorPos。
    private Timer? _poll;
    private long _pollHits;
    private long _pollKbHits;
    private readonly bool[] _vkDown = new bool[256];
    private readonly bool[] _mouseDown = new bool[5];
    private int _lastPollX = int.MinValue;
    private int _lastPollY = int.MinValue;
    private const int PollIntervalMs = 20;
    private readonly Dictionary<string, long> _recentFrames = new();
    private const int DedupMs = 35;          // 同一事件两路都会到，指纹相同则在窗口内只发一次

    /// <summary>低级钩子收到的事件数（诊断：游戏里这个数不涨、raw 涨 → 钩子被挡了）。</summary>
    public long HookHits => Interlocked.Read(ref _hookHits);

    /// <summary>Raw Input 收到的事件数。</summary>
    public long RawHits => Interlocked.Read(ref _rawHits);

    /// <summary>键盘事件分别来自哪条路（鼠标移动噪声大，键盘计数才看得准）。</summary>
    public long HookKbHits => Interlocked.Read(ref _hookKbHits);

    /// <summary>钩子回调次数（0 说明这个环境里系统没有回调我们，钩子被挡在门外）。</summary>
    public long HookCalls => Interlocked.Read(ref _hookCalls);

    /// <summary>两个钩子是否安装成功（null 句柄 = SetWindowsHookEx 失败）。</summary>
    public bool HooksInstalled => _kbHook != IntPtr.Zero || _msHook != IntPtr.Zero;

    public string HooksDetail => "kb=" + (_kbHook != IntPtr.Zero ? "ok" : "null") + " ms=" + (_msHook != IntPtr.Zero ? "ok" : "null");

    public long RawKbHits => Interlocked.Read(ref _rawKbHits);

    /// <summary>轮询路收到的事件数（键盘 + 鼠标）。</summary>
    public long PollHits => Interlocked.Read(ref _pollHits);

    public long PollKbHits => Interlocked.Read(ref _pollKbHits);

    /// <summary>累计派发的事件帧数（诊断用：按键时这个数在涨说明钩子活着）。</summary>
    public long Events => Interlocked.Read(ref _events);

    /// <summary>重挂次数（>0 说明钩子曾被系统摘掉并被看护恢复）。</summary>
    public int Reinstalls => _reinstalls;

    /// <summary>距最后一次收到事件过去了多久（毫秒；-1 = 尚未收到过）。</summary>
    public long LastEventAgoMs
    {
        get
        {
            var t = Interlocked.Read(ref _lastEventTicks);
            return t == 0 ? -1 : Environment.TickCount64 - t;
        }
    }

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
        _watch ??= new Timer(_ => Watch(), null, WatchIntervalMs, WatchIntervalMs);
        _poll ??= new Timer(_ => PollInput(), null, PollIntervalMs, PollIntervalMs);
    }

    public void Stop()
    {
        try { _watch?.Dispose(); } catch { }
        _watch = null;
        try { _poll?.Dispose(); } catch { }
        _poll = null;
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
        _thread = null;
        _threadId = 0;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        InstallHooks();
        EnableRawInput();     // 第二路：钩子被挡时靠它
        // Message pump keeps the LL hooks alive on this thread.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmReinstall)
            {
                // 只在本线程重挂：低级钩子的回调固定在安装它的线程上执行
                UninstallHooks();
                InstallHooks();
                Interlocked.Increment(ref _reinstalls);
                Interlocked.Exchange(ref _lastEventTicks, Environment.TickCount64);   // 重新计时，避免连环重挂
                Interlocked.Exchange(ref _lastHookHitTicks, Environment.TickCount64);
                ServiceLog.Info("键鼠", "输入钩子被系统摘掉，已重挂（第 " + _reinstalls + " 次）");
                continue;
            }
            _ = TranslateMessage(ref msg);
            _ = DispatchMessage(ref msg);
        }
        UninstallHooks();
        DisableRawInput();
    }

    /// <summary>手动重挂（诊断/兜底）：走与看护同一条路，保证在钩子线程上执行。</summary>
    public void Reinstall()
    {
        var t = _threadId;
        if (t != 0) PostThreadMessage(t, WmReinstall, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 起一个**独立线程**承载 Raw Input 的消息窗口与消息泵。
    /// 关键：不能放在钩子线程上 —— 鼠标移动的 WM_INPUT 是 500~1000Hz 的洪流，
    /// 会把钩子线程占住，导致低级钩子回调赶不上系统的 LowLevelHooksTimeout 而被静默摘掉
    /// （实测：钩子计数恒为 0、重挂也救不回来，而 Raw/轮询照常在涨）。
    /// </summary>
    private void EnableRawInput()
    {
        if (_rawThread != null) return;
        _rawThread = new Thread(RawRun) { IsBackground = true, Name = "KeyViewRaw" };
        _rawThread.SetApartmentState(ApartmentState.STA);
        _rawThread.Start();
    }

    private void DisableRawInput()
    {
        if (_rawThreadId != 0) PostThreadMessage(_rawThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        try { _rawThread?.Join(1500); } catch { }
        _rawThread = null;
        _rawThreadId = 0;
        _rawWnd = IntPtr.Zero;
    }

    private void RawRun()
    {
        try
        {
            _rawThreadId = GetCurrentThreadId();
            _wndProcKeepAlive = RawWndProc;
            // 用内建 STATIC 类建一个消息窗口（HWND_MESSAGE），再把窗口过程换掉：
            // 比注册自定义窗口类少一半代码，也不依赖 RegisterClassEx 的样式细节。
            _rawWnd = CreateWindowExW(0, "STATIC", "blt-keyview-rawinput", 0, 0, 0, 0, 0,
                                      HwndMessage, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (_rawWnd == IntPtr.Zero) return;
            _oldWndProc = SetWindowLongPtrW(_rawWnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive));

            var devs = new RAWINPUTDEVICE[2];
            devs[0].usUsagePage = 0x01; devs[0].usUsage = 0x06;   // generic desktop / keyboard
            devs[1].usUsagePage = 0x01; devs[1].usUsage = 0x02;   // generic desktop / mouse
            for (var i = 0; i < devs.Length; i++)
            {
                devs[i].dwFlags = RidevInputSink;
                devs[i].hwndTarget = _rawWnd;
            }
            RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref msg);
                _ = DispatchMessage(ref msg);
            }

            var off = new RAWINPUTDEVICE[2];
            off[0].usUsagePage = 0x01; off[0].usUsage = 0x06;
            off[1].usUsagePage = 0x01; off[1].usUsage = 0x02;
            for (var i = 0; i < off.Length; i++) off[i].dwFlags = RidevRemove;
            RegisterRawInputDevices(off, (uint)off.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch { /* 采集线程异常不影响主流程 */ }
    }

    private void InstallHooks()
    {
        _kbHook = SetWindowsHookExW(WhKeyboardLl, HookProcDelegate, GetModuleHandleW(null), 0);
        _msHook = SetWindowsHookExW(WhMouseLl, HookProcDelegate, GetModuleHandleW(null), 0);
    }

    private void UninstallHooks()
    {
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
        if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);
        _kbHook = _msHook = IntPtr.Zero;
    }

    /// <summary>
    /// 看护：系统记录的"最后一次输入"比我们最后一次收到的事件新出 --MissGraceMs-- 以上，
    /// 且系统那边确实刚刚有输入 → 认定钩子已被摘掉，请钩子线程重挂。
    /// </summary>
    private void Watch()
    {
        try { WatchCore(); }
        catch (Exception ex)
        {
            // Timer 回调里抛未处理异常会终止整个进程 —— 看护本身绝不能成为崩溃源
            try { ServiceLog.Info("键鼠", "钩子看护异常（已忽略）：" + ex.Message); } catch { }
        }
    }

    private void WatchCore()
    {
        var threadId = _threadId;
        if (threadId == 0) return;

        // 按路看护：别的路在收、而**钩子这一路**已经 3 秒没动静 → 钩子被摘了 → 重挂。
        // （只看"整体有没有事件"是不够的：Raw/轮询会一直喂事件，钩子死了也发现不了。）
        var now = Environment.TickCount64;
        var hookLast = Interlocked.Read(ref _lastHookHitTicks);
        var otherLast = Interlocked.Read(ref _lastOtherHitTicks);
        if (otherLast != 0 && now - otherLast <= MissGraceMs
            && (hookLast == 0 || otherLast > hookLast + MissGraceMs))
        {
            PostThreadMessage(threadId, WmReinstall, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        var last = Interlocked.Read(ref _lastEventTicks);
        if (last == 0) return;                       // 还没有任何事件，无从判断
        if (Environment.TickCount64 - last <= MissGraceMs) return;   // 我们也在收，正常

        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return;
        // dwTime 与 Environment.TickCount 同为 32 位 tick：用无符号差值规避 ~49.7 天回绕
        var sinceInputMs = unchecked((uint)(Environment.TickCount - (int)li.dwTime));
        if (sinceInputMs > InputFreshMs) return;     // 最近没人动键鼠 → 不能说明钩子坏了

        PostThreadMessage(threadId, WmReinstall, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var inst = _active;
        if (nCode >= 0 && inst != null)
        {
            Interlocked.Increment(ref inst._hookCalls);
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
                EmitKb("down", (int)s.vkCode, now, "hook");
                break;
            }
            case WmKeyUp or WmSysKeyUp:
            {
                var s = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                EmitKb("up", (int)s.vkCode, now, "hook");
                break;
            }
            case WmLbuttonDown or WmLbuttonUp:
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                EmitMs(msg == WmLbuttonDown ? "down" : "up", "left", s.pt.x, s.pt.y, now, "hook");
                break;
            }
            case WmRbuttonDown or WmRbuttonUp:
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                EmitMs(msg == WmRbuttonDown ? "down" : "up", "right", s.pt.x, s.pt.y, now, "hook");
                break;
            }
            case WmMbuttonDown or WmMbuttonUp:
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                EmitMs(msg == WmMbuttonDown ? "down" : "up", "middle", s.pt.x, s.pt.y, now, "hook");
                break;
            }
            case WmXbuttonDown or WmXbuttonUp:
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                // X1/X2 live in the high word of mouseData.
                var xb = (short)((s.mouseData >> 16) & 0xFFFF);
                EmitMs(msg == WmXbuttonDown ? "down" : "up", xb == 2 ? "x2" : "x1", s.pt.x, s.pt.y, now, "hook");
                break;
            }
            case WmMouseWheel or WmMouseHWheel:
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var delta = (short)((s.mouseData >> 16) & 0xFFFF);
                int dx = 0, dy = 0;
                if (msg == WmMouseHWheel) dx = delta / 120;
                else dy = delta / 120;
                EmitWheel(dx, dy, s.pt.x, s.pt.y, now, "hook");
                break;
            }
            case WmMouseMove:
            {
                var t = Environment.TickCount64;
                if (t - _lastMoveTicks < 16) return;
                _lastMoveTicks = t;
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                EmitMove(s.pt.x, s.pt.y, now, "hook");
                break;
            }
        }
    }

    private void EmitKb(string e, int vk, long ts, string src)
    {
        if (src == "raw") Interlocked.Increment(ref _rawKbHits);
        else if (src == "poll") Interlocked.Increment(ref _pollKbHits);
        else Interlocked.Increment(ref _hookKbHits);
        EmitDedup($"kb|{vk}|{e}", $"{{\"t\":\"kb\",\"e\":\"{e}\",\"k\":\"{KeyName(vk)}\",\"code\":{vk},\"ts\":{ts}}}", src);
    }

    private void EmitMs(string e, string button, int x, int y, long ts, string src)
        => EmitDedup($"ms|{button}|{e}|{x}|{y}", $"{{\"t\":\"ms\",\"e\":\"{e}\",\"b\":\"{button}\",\"x\":{x},\"y\":{y},\"ts\":{ts}}}", src);

    private void EmitWheel(int dx, int dy, int x, int y, long ts, string src)
        => EmitDedup($"ms|wheel|{dx}|{dy}", $"{{\"t\":\"ms\",\"e\":\"wheel\",\"dx\":{dx},\"dy\":{dy},\"x\":{x},\"y\":{y},\"ts\":{ts}}}", src);

    // 移动帧两路的坐标可能差一像素（一条取钩子结构、一条取 GetCursorPos），按坐标去重去不干净，
    // 于是只用固定指纹：16ms 节流内谁先到算谁的，另一路被丢掉。
    private void EmitMove(int x, int y, long ts, string src)
        => EmitDedup("ms|move", $"{{\"t\":\"ms\",\"e\":\"move\",\"x\":{x},\"y\":{y},\"ts\":{ts}}}", src);

    /// <summary>
    /// 两路（低级钩子 / Raw Input）在正常情况下都会收到同一个事件：按指纹在短窗口内去重，
    /// 保证浮层不会把一次按键画成两次；同时分别计数，便于判断哪条路在当前环境下有效。
    /// </summary>
    /// <summary>
    /// 轮询采集：每 20ms 读一次全键盘的异步状态与光标位置，状态变化才发帧（与另两路同指纹去重）。
    /// 它不依赖钩子、也不依赖消息投递，只读内核的按键状态，是"游戏里两条消息路都被挡"的兜底。
    /// </summary>
    private void PollInput()
    {
        try
        {
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            for (var vk = 1; vk < 256; vk++)
            {
                var down = (GetAsyncKeyState(vk) & 0x8000) != 0;
                if (down == _vkDown[vk]) continue;
                _vkDown[vk] = down;
                EmitKb(down ? "down" : "up", vk, now, "poll");
            }

            var buttons = new[] { (0x01, "left"), (0x02, "right"), (0x04, "middle"), (0x05, "x1"), (0x06, "x2") };
            for (var i = 0; i < buttons.Length; i++)
            {
                var down = (GetAsyncKeyState(buttons[i].Item1) & 0x8000) != 0;
                if (down == _mouseDown[i]) continue;
                _mouseDown[i] = down;
                var p = CursorPos();
                EmitMs(down ? "down" : "up", buttons[i].Item2, p.x, p.y, now, "poll");
            }

            var cur = CursorPos();
            if (cur.x != _lastPollX || cur.y != _lastPollY)
            {
                _lastPollX = cur.x;
                _lastPollY = cur.y;
                var t = Environment.TickCount64;
                if (t - _lastMoveTicks >= 16)
                {
                    _lastMoveTicks = t;
                    EmitMove(cur.x, cur.y, now, "poll");
                }
            }
        }
        catch { /* 轮询异常绝不能让进程挂掉 */ }
    }

    private IntPtr RawWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmInput)
        {
            try { HandleRawInput(lParam); } catch { /* 采集出错绝不能影响系统 */ }
        }
        return _oldWndProc != IntPtr.Zero
            ? CallWindowProcW(_oldWndProc, hWnd, msg, wParam, lParam)
            : DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void HandleRawInput(IntPtr hRaw)
    {
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        if (GetRawInputData(hRaw, RidInput, IntPtr.Zero, ref size, headerSize) != 0 || size == 0 || size > 256) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRaw, RidInput, buf, ref size, headerSize) != size) return;
            var head = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            var payload = buf + Marshal.SizeOf<RAWINPUTHEADER>();
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            if (head.dwType == RimTypeKeyboard)
            {
                var kb = Marshal.PtrToStructure<RAWKEYBOARD>(payload);
                if (kb.VKey == 0xFF || kb.VKey == 0) return;      // 占位按键（Pause/PrintScreen 等）
                EmitKb((kb.Flags & 1) != 0 ? "up" : "down", kb.VKey, now, "raw");
            }
            else if (head.dwType == RimTypeMouse)
            {
                var ms = Marshal.PtrToStructure<RAWMOUSE>(payload);
                var x = ms.lLastX;   // 相对坐标只用于"有没有动"的判断
                var y = ms.lLastY;
                var flags = ms.usButtonFlags;
                if (flags != 0)
                {
                    var pos = CursorPos();
                    if ((flags & 0x0001) != 0) EmitMs("down", "left", pos.x, pos.y, now, "raw");
                    if ((flags & 0x0002) != 0) EmitMs("up", "left", pos.x, pos.y, now, "raw");
                    if ((flags & 0x0004) != 0) EmitMs("down", "right", pos.x, pos.y, now, "raw");
                    if ((flags & 0x0008) != 0) EmitMs("up", "right", pos.x, pos.y, now, "raw");
                    if ((flags & 0x0010) != 0) EmitMs("down", "middle", pos.x, pos.y, now, "raw");
                    if ((flags & 0x0020) != 0) EmitMs("up", "middle", pos.x, pos.y, now, "raw");
                    if ((flags & 0x0040) != 0) EmitMs("down", "x1", pos.x, pos.y, now, "raw");
                    if ((flags & 0x0080) != 0) EmitMs("up", "x1", pos.x, pos.y, now, "raw");
                    if ((flags & 0x0100) != 0) EmitMs("down", "x2", pos.x, pos.y, now, "raw");
                    if ((flags & 0x0200) != 0) EmitMs("up", "x2", pos.x, pos.y, now, "raw");
                    if ((flags & 0x0400) != 0) EmitWheel(0, (short)ms.usButtonData / 120, pos.x, pos.y, now, "raw");
                    if ((flags & 0x0800) != 0) EmitWheel((short)ms.usButtonData / 120, 0, pos.x, pos.y, now, "raw");
                }
                // 鼠标**移动**故意不在这里处理：WM_INPUT 的移动是 500~1000Hz 的洪流，
                // 解析它会拖垮采集线程；位置变化交给 20ms 的轮询那一路（够用且便宜）。
                _ = x + y;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static POINT CursorPos()
    {
        return GetCursorPos(out var p) ? p : default;
    }

    private void EmitDedup(string fingerprint, string json, string src)
    {
        switch (src)
        {
            case "raw": Interlocked.Increment(ref _rawHits); break;
            case "poll": Interlocked.Increment(ref _pollHits); break;
            default: Interlocked.Increment(ref _hookHits); break;
        }

        var now = Environment.TickCount64;
        if (src == "hook") Interlocked.Exchange(ref _lastHookHitTicks, now);
        else Interlocked.Exchange(ref _lastOtherHitTicks, now);
        lock (_recentFrames)
        {
            if (_recentFrames.TryGetValue(fingerprint, out var last) && now - last < DedupMs) return;
            if (_recentFrames.Count > 128) _recentFrames.Clear();
            _recentFrames[fingerprint] = now;
        }
        Emit(json);
    }

    private void Emit(string json)
    {
        Interlocked.Exchange(ref _lastEventTicks, Environment.TickCount64);
        Interlocked.Increment(ref _events);
        _queue.Writer.TryWrite(json);
    }

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

    // PostThreadMessageW 由 winuser.h 声明 → user32.dll（原先误写成 kernel32：
    // 调用必然抛 EntryPointNotFoundException，被上层 try/catch 吞掉后表现为
    // 「Stop() 不生效，界面上关了键鼠可视化，钩子其实还挂着」）
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // ---------------- Raw Input ----------------
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags;
        public ushort usButtonFlags;
        public ushort usButtonData;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    private const int GwlWndProc = -4;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
                                                 int x, int y, int nWidth, int nHeight, IntPtr hWndParent,
                                                 IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
#endif
