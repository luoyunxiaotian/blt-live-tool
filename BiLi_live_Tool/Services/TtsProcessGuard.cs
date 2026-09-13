using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Keeps the standalone TTS servers (edge_tts_server.exe / moss_tts_server.exe)
/// from outliving the app:
///  - a Win32 Job Object with KILL_ON_JOB_CLOSE takes the whole process tree
///    down when this process dies — even on a hard kill or crash;
///  - a startup sweep kills orphaned servers whose ancestor chain is dead
///    (leaked by force-killed previous runs);
///  - a port probe lets us reuse a healthy server (e.g. started by the
///    Electron version) instead of spawning a duplicate.
/// </summary>
public static class TtsProcessGuard
{
    private static IntPtr _job;
    private static bool _jobReady;

    // ----- job object -----

    private static bool EnsureJob()
    {
        if (_jobReady) return _job != IntPtr.Zero;
        _jobReady = true;
        try
        {
            _job = CreateJobObjectW(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) return false;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    CloseHandle(_job);
                    _job = IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            _job = IntPtr.Zero;
        }
        return _job != IntPtr.Zero;
    }

    /// <summary>Assigns a spawned process to the kill-on-close job (children follow automatically).</summary>
    public static void GuardProcess(Process process)
    {
        try
        {
            if (!EnsureJob()) return;
            AssignProcessToJobObject(_job, process.Handle);
        }
        catch
        {
            // Job assignment is best-effort; the graceful-exit kill still applies.
        }
    }

    // ----- port probe -----

    /// <summary>True when something is already serving on the loopback port.</summary>
    public static bool IsPortAlive(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(400) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    // ----- orphan sweep -----

    private static readonly string[] TtsNames = { "edge_tts_server", "moss_tts_server" };

    /// <summary>
    /// Kills TTS servers whose ancestor chain no longer contains a live
    /// non-TTS process (leaked by force-killed or crashed runs). Servers owned
    /// by a live host (this app or the Electron version) are left alone.
    /// </summary>
    public static int KillOrphanedTtsServers()
    {
        var killed = 0;
        try
        {
            var procs = SnapshotProcesses();
            var alive = new HashSet<int>(procs.Keys);
            alive.Add(Environment.ProcessId);

            foreach (var (pid, ppid, name) in procs.Values)
            {
                if (!TtsNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (!IsOrphan(pid, procs, alive)) continue;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill(true);
                    killed++;
                }
                catch { }
            }
        }
        catch { }
        return killed;
    }

    /// <summary>Walks up the ancestor chain; orphan when we hit a dead PID before a live host.</summary>
    private static bool IsOrphan(int pid, Dictionary<int, (int Pid, int Ppid, string Name)> procs, HashSet<int> alive)
    {
        var cur = pid;
        for (var depth = 0; depth < 6; depth++)
        {
            if (!procs.TryGetValue(cur, out var info)) return true;   // chain broken → orphan
            var parent = info.Ppid;
            if (parent == 0) return true;
            if (!procs.TryGetValue(parent, out var parentInfo))
            {
                // Parent not in the snapshot: dead → orphan (unless it is this app, which is alive by definition).
                return parent != Environment.ProcessId;
            }
            if (!TtsNames.Contains(parentInfo.Name, StringComparer.OrdinalIgnoreCase))
                return false;   // reached a live non-TTS host → owned
            cur = parent;
        }
        return true;
    }

    // ----- process snapshot (Toolhelp32; no WMI dependency) -----

    private static Dictionary<int, (int Pid, int Ppid, string Name)> SnapshotProcesses()
    {
        var result = new Dictionary<int, (int, int, string)>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return result;
            do
            {
                var name = entry.szExeFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? entry.szExeFile[..^4]
                    : entry.szExeFile;
                result[(int)entry.th32ProcessID] = ((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, name);
            } while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    // ----- interop -----

    private const uint Th32CsSnapProcess = 0x00000002;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
