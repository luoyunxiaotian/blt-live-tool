using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Config store holding the full original config schema. Services/default-config.json
/// is generated from Bin/lib/config.js (defaultConfig + all text pools), so field
/// fidelity matches the Electron version. Mirrors ensureDefaults(): defaults are
/// deep-merged under the stored user config; the raw document is served to the
/// legacy panel via /api/config exactly like Bin/server.js did.
/// </summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private JsonObject _doc;

    public AppConfig()
    {
        _doc = LoadDocument();
    }

    /// <summary>
    /// Layout root of the installation (null when the flat/portable layout is in use).
    /// The installer-based layout (v0.1.4+) ships <c>app\layout.json</c> next to the exe:
    /// <c>{"layout":2,"dataDir":"..\data"}</c>. With it, user data lives at the layout root —
    /// the exe folder's parent — so the install root stays tidy (launcher + app\ + data\ +
    /// readme) while the exe keeps all its dependencies beside it (the .NET loader requires
    /// that). Portable builds have no marker and keep data next to the exe exactly as before.
    /// </summary>
    private static readonly Lazy<string?> LayoutRoot = new(ReadLayoutRoot);

    private static string? ReadLayoutRoot()
    {
        try
        {
            var marker = Path.Combine(AppContext.BaseDirectory, "layout.json");
            if (!File.Exists(marker)) return null;
            if (JsonNode.Parse(File.ReadAllText(marker)) is not JsonObject o) return null;
            // 只有明确声明了 layout>=2 才认这个标记，避免误读别的 json
            if (o["layout"]?.GetValue<int?>() is not int n || n < 2) return null;
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        }
        catch { return null; }
    }

    /// <summary>Where the installation starts: the layout root when a marker exists, else the exe folder.</summary>
    public static string InstallRoot => LayoutRoot.Value ?? AppContext.BaseDirectory;

    public static string DataDir => Path.Combine(InstallRoot, "data");
    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string WwwRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot");
    public static string LegacyRoot => Path.Combine(WwwRoot, "legacy");

    private JsonObject LoadDocument()
    {
        var defaults = new JsonObject();
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "default-config.json");
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject d)
                defaults = d;
        }
        catch { /* fall back to an empty defaults layer */ }

        // Rewrite paths for the MAUI layout (generated file ships them blank), and pin
        // the demo port: the generated defaults carry the Electron original's 7360,
        // which must not be stolen from a running old version.
        defaults["port"] = 7460;
        defaults["dataRoot"] = DataDir;
        if (defaults["folders"] is not JsonObject folders) folders = new JsonObject();
        foreach (var type in new[] { "danmu", "gifts", "blindbox", "guard", "superchat" })
            folders[type] = Path.Combine(DataDir, type);
        defaults["folders"] = folders;

        JsonObject stored = new();
        try
        {
            if (File.Exists(ConfigPath) && JsonNode.Parse(File.ReadAllText(ConfigPath)) is JsonObject s)
                stored = s;
        }
        catch { /* corrupt file → defaults only */ }

        var doc = DeepMerge(defaults, stored);
        if (_GetLong(doc, "port") <= 0) doc["port"] = 7460;
        return doc;
    }

    private static long _GetLong(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<long>(out var l) ? l : 0;

    private static string _GetStr(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<string>(out var s) ? s ?? "" : "";

    /// <summary>Stored values win; defaults fill everything missing (recursing into objects).</summary>
    private static JsonObject DeepMerge(JsonObject defaults, JsonObject stored)
    {
        var merged = new JsonObject();
        foreach (var (key, defVal) in defaults)
            merged[key] = defVal?.DeepClone();
        foreach (var (key, storedVal) in stored)
        {
            if (storedVal is null) { merged[key] = null; continue; }
            if (merged[key] is JsonObject mo && storedVal is JsonObject so)
                merged[key] = DeepMerge(mo, so);
            else
                merged[key] = storedVal.DeepClone();
        }
        return merged;
    }

    // ----- typed accessors used by C# services -----

    public int Port { get { lock (_lock) { var p = _GetLong(_doc, "port"); return p > 0 ? (int)p : 7460; } } }
    public string RoomId { get { lock (_lock) return _GetStr(_doc, "roomId"); } }
    public string Cookie { get { lock (_lock) return _GetStr(_doc, "cookie"); } }
    public bool AutoConnect { get { lock (_lock) return _doc.TryGetPropertyValue("autoConnect", out var v) && v is JsonValue bv && bv.TryGetValue<bool>(out var b) && b; } }
    public string Uid { get { lock (_lock) { return _doc.TryGetPropertyValue("uid", out var v) && v is JsonValue val && val.TryGetValue<string>(out var s) ? s ?? "" : ""; } } }

    public string NowPlayingStyle
    {
        get { lock (_lock) return _GetStr(_doc, "nowPlayingStyle") is var s && s.Length > 0 ? s : "vinyl"; }
        set { lock (_lock) { _doc["nowPlayingStyle"] = value; SaveNoLock(); } }
    }
    public string NowPlayingTheme
    {
        get { lock (_lock) return _GetStr(_doc, "nowPlayingTheme") is var s && s.Length > 0 ? s : "dark"; }
        set { lock (_lock) { _doc["nowPlayingTheme"] = value; SaveNoLock(); } }
    }
    public bool NowPlayingAutoHide
    {
        get { lock (_lock) return _doc.TryGetPropertyValue("nowPlayingAutoHide", out var v) && v is JsonValue bv && bv.TryGetValue<bool>(out var b) && b; }
        set { lock (_lock) { _doc["nowPlayingAutoHide"] = value; SaveNoLock(); } }
    }
    public double NowPlayingScale
    {
        get { lock (_lock) return _doc.TryGetPropertyValue("nowPlayingScale", out var v) && v is JsonValue nv && nv.TryGetValue<double>(out var d) && d > 0 ? d : 1.0; }
        set { lock (_lock) { _doc["nowPlayingScale"] = value; SaveNoLock(); } }
    }
    public bool PreferInternalPlayer
    {
        get { lock (_lock) return !_doc.TryGetPropertyValue("preferInternalPlayer", out var v) || (v is JsonValue bv && (!bv.TryGetValue<bool>(out var b) || b)); }
        set { lock (_lock) { _doc["preferInternalPlayer"] = value; SaveNoLock(); } }
    }
    public bool IgnoreBrowsers
    {
        get { lock (_lock) return !_doc.TryGetPropertyValue("ignoreBrowsers", out var v) || (v is JsonValue bv && (!bv.TryGetValue<bool>(out var b) || b)); }
        set { lock (_lock) { _doc["ignoreBrowsers"] = value; SaveNoLock(); } }
    }

    public void SetRoomAndCookie(string roomId, string cookie)
    {
        lock (_lock)
        {
            _doc["roomId"] = roomId;
            if (cookie.Length > 0) _doc["cookie"] = cookie;
            SaveNoLock();
        }
    }

    /// <summary>Cookie captured from the embedded bilibili browser (MainPage polling).</summary>
    public void SetBiliCookie(string cookie, string uid)
    {
        lock (_lock)
        {
            _doc["cookie"] = cookie;
            if (!string.IsNullOrEmpty(uid)) _doc["uid"] = uid;
            SaveNoLock();
        }
    }

    public void SetRecording(string type, bool on)
    {
        lock (_lock)
        {
            if (_doc["recording"] is not JsonObject rec) rec = new JsonObject();
            rec[type] = on;
            _doc["recording"] = rec;
            SaveNoLock();
        }
    }

    /// <summary>Top-level merge of a client-posted config patch (like POST /api/config in server.js); port stays pinned.</summary>
    public void UpdateFrom(JsonObject patch)
    {
        var port = Port;
        lock (_lock)
        {
            foreach (var (key, val) in patch)
                _doc[key] = val?.DeepClone();
            _doc["port"] = port; // running server owns the port; ignore client-side changes for now
            SaveNoLock();
        }
    }

    /// <summary>Read the boolean "&lt;section&gt;.enabled" flag (missing key → fallback).</summary>
    public bool SectionEnabled(string section, bool fallback = false)
    {
        lock (_lock)
        {
            if (!_doc.TryGetPropertyValue(section, out var node) || node is not JsonObject sec) return fallback;
            return sec.TryGetPropertyValue("enabled", out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b) ? b : fallback;
        }
    }

    /// <summary>
    /// Flip one section's "enabled" flag and persist, leaving every sibling key alone —
    /// UpdateFrom replaces whole top-level sections, so a partial patch would drop them.
    /// Callers that need the change to reach running services call ApplyConfig afterwards.
    /// </summary>
    public void SetSectionEnabled(string section, bool value)
    {
        lock (_lock)
        {
            if (!_doc.TryGetPropertyValue(section, out var node) || node is not JsonObject sec)
            {
                sec = new JsonObject();
                _doc[section] = sec;
            }
            sec["enabled"] = value;
            SaveNoLock();
        }
    }

    /// <summary>Swap the whole document (mutated snapshot workflow) and persist; port stays pinned.</summary>
    public void ReplaceFrom(JsonObject doc)
    {
        var port = Port;
        lock (_lock)
        {
            _doc = doc;
            _doc["port"] = port;
            SaveNoLock();
        }
    }

    /// <summary>Clone of a top-level section (null when absent).</summary>
    public JsonNode? GetNode(string key)
    {
        lock (_lock) return _doc[key]?.DeepClone();
    }

    /// <summary>Deep copy of the whole document for serving via /api/config.</summary>
    public JsonObject Snapshot()
    {
        lock (_lock)
            return JsonNode.Parse(_doc.ToJsonString())!.AsObject();
    }

    /// <summary>Clones of the recording flags and folders mapping for /api/status payloads.</summary>
    public (JsonNode? Recording, JsonNode? Folders) RecordingAndFolders()
    {
        lock (_lock)
            return (_doc["recording"]?.DeepClone(), _doc["folders"]?.DeepClone());
    }

    public string SnapshotJson()
    {
        lock (_lock) return _doc.ToJsonString();
    }

    private void SaveNoLock()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ConfigPath, _doc.ToJsonString(Pretty));
    }
}
