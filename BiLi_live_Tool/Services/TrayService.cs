// TrayService.cs — Win32 system tray icon via Shell_NotifyIconW (no NuGet packages).
// Ported (rewritten, not copied) from the Electron original Bin/src/tray.js.
//
// Port scope / differences vs. the Electron original:
//   - Original loads custom tray PNG/ICO files from resources/build; this rewrite uses
//     the default application icon (LoadIcon(NULL, IDI_APPLICATION)) until a proper
//     packaged .ico is wired up for the MAUI build.
//   - Original menu has status text, service start/stop, data dir, auto-launch checkbox;
//     this rewrite is trimmed to the two actions the coordinator wires up:
//     "显示/隐藏窗口" (toggle main window) and "退出" (quit).
//   - Original posts a first-run system notification; not ported here.
//
// Threading requirements (IMPORTANT):
//   - Initialize() MUST be called on the UI (main) thread. The hidden callback window is
//     created on that thread and is served by the WinUI message pump that already runs
//     there — this file deliberately does NOT spawn its own message loop thread.
//   - Because the window lives on the UI thread, the onToggleVisible / onQuit callbacks
//     are invoked synchronously on the UI thread. The callbacks themselves are expected
//     to handle any further thread marshalling they need.
//   - Call Shutdown() from the UI thread as well.

#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Static system tray icon backed by raw Win32 P/Invoke (Shell_NotifyIconW). A hidden
/// message window receives the tray callback message (WM_APP) and the popup menu's
/// WM_COMMAND, then invokes the delegates supplied to <see cref="Initialize"/>.
/// </summary>
public static class TrayService
{
    private const string WindowClassName = "BiLiLiveToolTray";

    // Tray callback message. WM_APP (0x8000) is the recommended base for private
    // window messages (the task brief calls it "WM_USER+1 style, e.g. 0x8000").
    private const uint WmTrayCallback = 0x8000;

    private const uint TrayIconId = 1;

    // Context menu command ids (arrive as WM_COMMAND with LOWORD(wParam) == id).
    private const int MenuToggleWindow = 1;
    private const int MenuQuit = 2;

    // Win32 message constants.
    private const uint WmCommand = 0x0111;
    private const uint WmNull = 0x0000;
    private const uint WmLbuttonUp = 0x0202;
    private const uint WmRbuttonUp = 0x0205;

    // Shell_NotifyIcon messages / NOTIFYICONDATA flags.
    private const uint NimAdd = 0x0000;
    private const uint NimModify = 0x0001;
    private const uint NimDelete = 0x0002;
    private const uint NifMessage = 0x0001;
    private const uint NifIcon = 0x0002;
    private const uint NifTip = 0x0004;

    // Menu / popup flags.
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmBottomAlign = 0x0020;

    // ERROR_CLASS_ALREADY_EXISTS — RegisterClassExW on a re-Initialize is benign.
    private const int ErrorClassAlreadyExists = 1410;

    private const IntPtr IdiApplication = (IntPtr)32512;

    private static bool _initialized;
    private static IntPtr _hwnd;
    private static Action? _onToggleVisible;
    private static Action? _onQuit;

    /// <summary>Diagnostics for the optional-tray silent-failure path.</summary>
    public static string LastDebug { get; private set; } = "not-run";

    // Keep the window-proc delegate alive for the process lifetime: the native side
    // stores the raw function pointer in the window class. A static readonly field on
    // a static class is never collected, which is exactly what we need.
    private static readonly WndProcDelegate WndProcCallback = WndProcInner;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Creates the hidden callback window, registers the tray icon and wires the
    /// callbacks. Safe to call once per app lifetime; repeat calls are no-ops while
    /// initialized, and a re-call after <see cref="Shutdown"/> re-creates the tray.
    /// Must be called on the UI thread (see file header).
    /// </summary>
    /// <param name="title">Tooltip text shown when hovering the tray icon.</param>
    /// <param name="onToggleVisible">Invoked on left click and on "显示/隐藏窗口".</param>
    /// <param name="onQuit">Invoked on the "退出" menu item.</param>
    public static void Initialize(string title, Action? onToggleVisible, Action? onQuit)
    {
        if (_initialized)
            return;

        _onToggleVisible = onToggleVisible;
        _onQuit = onQuit;

        try
        {
            IntPtr hInstance = GetModuleHandleW(null);

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcCallback),
                hInstance = hInstance,
                lpszClassName = WindowClassName,
            };

            ushort atom = RegisterClassExW(ref wc);
            if (atom == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            {
                LastDebug = "register-fail err=" + Marshal.GetLastWin32Error();
                return; // Tray is optional; fail silently and keep the app usable.
            }

            // Hidden top-level window: never shown, exists only to receive the tray
            // callback message and menu WM_COMMAND. Its messages are dispatched by
            // the WinUI message pump already running on the UI thread.
            _hwnd = CreateWindowExW(
                0, WindowClassName, WindowClassName, 0,
                0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                LastDebug = "createwindow-fail err=" + Marshal.GetLastWin32Error();
                return;
            }

            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = TrayIconId,
                uFlags = NifMessage | NifIcon | NifTip,
                uCallbackMessage = WmTrayCallback,
                hIcon = LoadIcon(IntPtr.Zero, IdiApplication),
                szTip = TruncateForTip(title),
            };

            // NIM_ADD can fail when a stale icon from a crashed previous run is still
            // parked in the tray; NIM_MODIFY re-attaches to the existing entry then.
            if (!Shell_NotifyIconW(NimAdd, ref nid) && !Shell_NotifyIconW(NimModify, ref nid))
            {
                LastDebug = "nim-add-and-modify-fail";
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                return;
            }

            LastDebug = "ok hwnd=" + _hwnd;
            _initialized = true;
        }
        catch (Exception ex)
        {
            // Tray is an optional convenience; never let a failure here take the app down.
            LastDebug = "exception: " + ex.Message;
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Removes the tray icon (NIM_DELETE) and destroys the hidden callback window.
    /// Must be called on the UI thread (see file header).
    /// </summary>
    public static void Shutdown()
    {
        if (_hwnd != IntPtr.Zero)
        {
            var nid = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = TrayIconId,
            };

            Shell_NotifyIconW(NimDelete, ref nid);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _onToggleVisible = null;
        _onQuit = null;
        _initialized = false;
    }

    private static IntPtr WndProcInner(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmTrayCallback:
                HandleTrayMouse(lParam);
                break;

            case WmCommand:
                int id = unchecked((int)(long)wParam) & 0xFFFF;
                if (id == MenuToggleWindow)
                    _onToggleVisible?.Invoke();
                else if (id == MenuQuit)
                    _onQuit?.Invoke();
                break;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static void HandleTrayMouse(IntPtr lParam)
    {
        uint mouseMsg = (uint)((long)lParam & 0xFFFF);
        if (mouseMsg == WmLbuttonUp)
        {
            _onToggleVisible?.Invoke();
        }
        else if (mouseMsg == WmRbuttonUp)
        {
            ShowContextMenu();
        }
    }

    private static void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenuW(menu, MfString, (IntPtr)MenuToggleWindow, "显示/隐藏窗口");
            AppendMenuW(menu, MfString, (IntPtr)MenuQuit, "退出");

            GetCursorPos(out POINT pt);

            // The window must be the foreground window before TrackPopupMenu,
            // otherwise the menu does not dismiss when the user clicks elsewhere.
            // The trailing WM_NULL post is the classic fix that lets the next tray
            // activation work reliably.
            SetForegroundWindow(_hwnd);
            TrackPopupMenu(menu, TpmRightButton | TpmBottomAlign, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static string TruncateForTip(string? title)
    {
        const int MaxChars = 127; // szTip holds 128 wchars incl. terminator
        if (string.IsNullOrEmpty(title))
            return string.Empty;
        return title.Length <= MaxChars ? title : title[..MaxChars];
    }

    // ---------- Win32 interop ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion; // union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
#else
using System;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Non-Windows stub. Same public surface as the Windows implementation so shared
/// wiring code compiles for every target framework; all members are no-ops.
/// </summary>
public static class TrayService
{
    public static void Initialize(string title, Action? onToggleVisible, Action? onQuit)
    {
        // No tray support on non-Windows targets.
    }

    public static void Shutdown()
    {
        // No tray support on non-Windows targets.
    }
}
#endif
