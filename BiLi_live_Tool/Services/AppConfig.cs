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

    public static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
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
