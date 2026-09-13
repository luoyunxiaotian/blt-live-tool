using System.Diagnostics;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Port of the TTS process management in src/main.js: spawns the standalone
/// edge_tts_server.exe (port 8020, always) and moss_tts_server.exe (port 8021,
/// on demand — model dir under the data root). The Kestrel /api/tts/* proxy
/// forwards to whichever engine is running, so the untouched frontend keeps
/// working. Exe search: %BLT_TTS_DIR% → &lt;app&gt;\tts → dev fallback Bin\bin.
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
            return Process.Start(psi);
        }
        catch
        {
            return null;
        }
    }

    public void StartEdge()
    {
        lock (_lock)
        {
            if (_edge is { HasExited: false }) return;
            var exe = FindExe("edge_tts_server.exe");
            if (exe == null) return;   // panel falls back to built-in / system voices
            _edge = Spawn(exe, EdgePort.ToString());
            _edgePath = exe;
        }
    }

    public object EdgeStatus()
    {
        lock (_lock)
            return new { running = _edge is { HasExited: false }, port = EdgePort, execPath = _edgePath };
    }

    public object EnsureMoss()
    {
        lock (_lock)
        {
            if (_moss is { HasExited: false }) return MossStatusNoLock();
            var exe = FindExe("moss_tts_server.exe");
            if (exe == null) return MossStatusNoLock();
            try { Directory.CreateDirectory(ModelDir); } catch { }
            _moss = Spawn(exe, $"{MossPort} \"{ModelDir}\"");
            _mossPath = exe;
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

    public object MossStatus()
    {
        lock (_lock) return MossStatusNoLock();
    }

    private object MossStatusNoLock()
        => new { running = _moss is { HasExited: false }, port = MossPort, execPath = _mossPath, modelDir = ModelDir };

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
