using System.Text.Json;
using System.Text.Json.Nodes;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Port of src/keyview/config.js: flat dotted-key config (default.json merged
/// under user overrides), change subscription for {t:'cfg'} pushes and a
/// debounced write of the user layer to the data dir.
/// </summary>
public sealed class KeyViewConfigStore
{
    private const string ValueName = "keyview-config.json";

    private readonly object _lock = new();
    private JsonObject _merged = new();
    private JsonObject _user = new();
    private System.Threading.Timer? _writeTimer;

    /// <summary>key='*' → full config pushed; otherwise the single dotted key.</summary>
    public event Action<string>? Changed;

    public KeyViewConfigStore()
    {
        Load();
    }

    private void Load()
    {
        JsonObject defaults = new(), user = new();
        try
        {
            var defPath = Path.Combine(AppContext.BaseDirectory, "keyview-default.json");
            if (File.Exists(defPath) && JsonNode.Parse(File.ReadAllText(defPath)) is JsonObject d) defaults = d;
        }
        catch { }
        try
        {
            var userPath = Path.Combine(AppConfig.DataDir, ValueName);
            if (File.Exists(userPath) && JsonNode.Parse(File.ReadAllText(userPath)) is JsonObject u) user = u;
        }
        catch { }
        lock (_lock)
        {
            _user = user;
            _merged = DeepMerge(defaults, user);
        }
    }

    private static JsonObject DeepMerge(JsonObject defaults, JsonObject overrides)
    {
        var merged = new JsonObject();
        foreach (var (k, v) in defaults) merged[k] = v?.DeepClone();
        foreach (var (k, v) in overrides) merged[k] = v?.DeepClone();
        return merged;
    }

    public JsonObject GetAll()
    {
        lock (_lock) return _merged.DeepClone()!.AsObject();
    }

    public void Set(string key, JsonNode? value)
    {
        bool changed;
        JsonNode? val;
        lock (_lock)
        {
            _merged[key] = value?.DeepClone();
            _user[key] = value?.DeepClone();
            val = _merged[key]?.DeepClone();
            changed = true;
        }
        ScheduleWrite();
        if (changed) Changed?.Invoke(key);
    }

    public void SetAll(JsonObject obj)
    {
        JsonObject defaults;
        lock (_lock)
        {
            // Re-merge from defaults so removed override keys fall back.
            var defPath = Path.Combine(AppContext.BaseDirectory, "keyview-default.json");
            defaults = File.Exists(defPath) && JsonNode.Parse(File.ReadAllText(defPath)) is JsonObject d ? d : new JsonObject();
            _merged = DeepMerge(defaults, obj);
            _user = obj.DeepClone()!.AsObject();
        }
        ScheduleWrite();
        Changed?.Invoke("*");
    }

    private void ScheduleWrite()
    {
        lock (_lock)
        {
            _writeTimer?.Dispose();
            _writeTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    string json;
                    lock (_lock) json = _user.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    Directory.CreateDirectory(AppConfig.DataDir);
                    File.WriteAllText(Path.Combine(AppConfig.DataDir, ValueName), json);
                }
                catch { }
            }, null, 300, Timeout.Infinite);
        }
    }
}
