using System.Diagnostics;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Port of the TTS process management in src/main.js: spawns the standalone
/// edge_tts_server.exe (port 8020, always) and moss_tts_server.exe (port 8021,
/// on demand — model dir under the data root). The Kestrel /api/tts/* proxy
/// forwards to whichever engine is running, so the untouched frontend keeps
/// working. Exe search: %BLT_TTS_DIR% → &lt;app&gt;\tts → dev fallback Bin\bin.
///
/// edge ships with the app; the 44.5 MB moss exe is installed on demand by
/// <see cref="MossEngineInstaller"/> into &lt;app&gt;\tts, which <see cref="FindMossExe"/>
/// picks up because the path is probed on every EnsureMoss() call.
/// </summary>
public sealed class TtsHost
{
    public const int EdgePort = 8020;
    public const int MossPort = 8021;

    private readonly object _lock = new();
    private Process? _edge;
    private Process? _moss;
    private string _edgePath = "";
    private string _mossPath = "";
    private bool _edgeReused;
    private bool _mossReused;
    private static bool _swept;

    public static string ModelDir => Path.Combine(AppConfig.DataDir, "tts-moss");

    private static IEnumerable<string> CandidateDirs()
    {
        var env = Environment.GetEnvironmentVariable("BLT_TTS_DIR");
        if (!string.IsNullOrEmpty(env)) yield return env;
        yield return Path.Combine(AppContext.BaseDirectory, "tts");
        // Dev fallback: reuse the Electron build's exes on the same machine.
        yield return @"H:\直播插件\Bin\bin";
    }

    private static string? FindExe(string name)
        => CandidateDirs().Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    /// <summary>
    /// Full path of a usable moss_tts_server.exe, or null when the engine is not
    /// installed (the app packages no moss exe any more — see MossEngineInstaller).
    /// </summary>
    internal static string? FindMossExe() => FindExe("moss_tts_server.exe");

    private static Process? Spawn(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var p = Process.Start(psi);
            if (p != null) TtsProcessGuard.GuardProcess(p);   // die with us, even on hard kill
            return p;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One-time cleanup of TTS servers leaked by force-killed earlier runs.</summary>
    private static void SweepOrphansOnce()
    {
        if (_swept) return;
        _swept = true;
        try
        {
            var killed = TtsProcessGuard.KillOrphanedTtsServers();
            if (killed > 0) System.Diagnostics.Debug.WriteLine($"[tts] swept {killed} orphaned server process(es)");
        }
        catch { }
    }

    public void StartEdge()
    {
        lock (_lock)
        {
            if (_edge is { HasExited: false }) return;
            SweepOrphansOnce();
            // Reuse a healthy server when one is already serving (e.g. the
            // Electron version started it, or a previous instance survived).
            if (TtsProcessGuard.IsPortAlive(EdgePort))
            {
                _edgeReused = true;
                _edgePath = "(reused)";
                return;
            }
            var exe = FindExe("edge_tts_server.exe");
            if (exe == null) return;   // panel falls back to built-in / system voices
            _edge = Spawn(exe, EdgePort.ToString());
            _edgePath = exe;
            _edgeReused = false;
        }
    }

    public object EdgeStatus()
    {
        lock (_lock)
            return new { running = _edge is { HasExited: false } || (_edgeReused && TtsProcessGuard.IsPortAlive(EdgePort)), port = EdgePort, execPath = _edgePath };
    }

    public object EnsureMoss()
    {
        lock (_lock)
        {
            if (_moss is { HasExited: false }) return MossStatusNoLock();
            SweepOrphansOnce();
            if (TtsProcessGuard.IsPortAlive(MossPort))
            {
                _mossReused = true;
                _mossPath = "(reused)";
                return MossStatusNoLock();
            }
            var exe = FindMossExe();   // re-probed on every call → a freshly downloaded engine is found
            if (exe == null) return MossStatusNoLock();
            try { Directory.CreateDirectory(ModelDir); } catch { }
            _moss = Spawn(exe, $"{MossPort} \"{ModelDir}\"");
            _mossPath = exe;
            _mossReused = false;
            return MossStatusNoLock();
        }
    }

    public object StopMoss()
    {
        Process? toKill;
        lock (_lock)
        {
            toKill = _moss;
            _moss = null;
        }
        try { toKill?.Kill(true); } catch { }
        toKill?.Dispose();
        return MossStatus();
    }

    public async Task<object> RestartMossAsync()
    {
        StopMoss();
        await Task.Delay(1200);   // let the port settle, same as main.js
        return EnsureMoss();
    }

    /// <summary>
    /// Re-probes the exe search paths after the on-demand engine download
    /// (MossEngineInstaller). A running server keeps the path it was spawned from;
    /// only a dead / not-started cache entry is refreshed, so a freshly installed
    /// exe shows up in the status payload without restarting the app. Cheap (a few
    /// File.Exists probes) and safe to call from the UI thread.
    /// </summary>
    public void RescanEngines()
    {
        lock (_lock)
        {
            if (_edge is not { HasExited: false } && !_edgeReused) _edgePath = FindExe("edge_tts_server.exe") ?? "";
            if (_moss is not { HasExited: false } && !_mossReused) _mossPath = FindMossExe() ?? "";
        }
    }

    public object MossStatus()
    {
        lock (_lock) return MossStatusNoLock();
    }

    private object MossStatusNoLock()
        => new
        {
            running = _moss is { HasExited: false } || (_mossReused && TtsProcessGuard.IsPortAlive(MossPort)),
            port = MossPort,
            execPath = _mossPath,
            modelDir = ModelDir,
            // true = engine exe present on disk (starting it can work), false = still needs the
            // on-demand download; lets the panel tell 「未安装」 apart from 「已安装未启动」.
            engineInstalled = FindMossExe() != null,
        };

    public void StopAll()
    {
        Process? edge = null, moss = null;
        lock (_lock)
        {
            edge = _edge; moss = _moss;
            _edge = null; _moss = null;
        }
        try { edge?.Kill(true); } catch { }
        try { moss?.Kill(true); } catch { }
        edge?.Dispose();
        moss?.Dispose();
    }
}
