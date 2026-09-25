#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BiLi_live_Tool.Services.SystemMedia;

public static class WindowDetector
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    public static string GetWindowTitleByHandle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return string.Empty;
        int length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetWindowTitle(string processName)
    {
        string windowTitle = string.Empty;
        bool found = false;

        EnumWindows((hWnd, lParam) =>
        {
            if (found) return false;

            GetWindowThreadProcessId(hWnd, out uint processId);

            if (IsWindowVisible(hWnd))
            {
                try
                {
                    string procName = Process.GetProcessById((int)processId).ProcessName;
                    if (string.Equals(procName, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        windowTitle = GetWindowTitleByHandle(hWnd);
                        return false;
                    }
                }
                catch { }
            }

            return true;
        }, IntPtr.Zero);

        return windowTitle;
    }

    public static List<string> GetWindowTitles(string processName)
    {
        var windowTitles = new List<string>();
        Process[] processes = Process.GetProcessesByName(processName);
        if (processes.Length == 0) return windowTitles;

        var pids = new HashSet<uint>();
        foreach (var p in processes)
        {
            pids.Add((uint)p.Id);
        }

        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);

            if (pids.Contains(pid))
            {
                string title = GetWindowTitleByHandle(hWnd);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    windowTitles.Add(title);
                }
            }

            return true;
        }, IntPtr.Zero);

        return windowTitles;
    }
}
#else
namespace BiLi_live_Tool.Services.SystemMedia;

public static class WindowDetector
{
    public static string GetWindowTitle(string processName) => "";
    public static System.Collections.Generic.List<string> GetWindowTitles(string processName) => new();
}
#endif
