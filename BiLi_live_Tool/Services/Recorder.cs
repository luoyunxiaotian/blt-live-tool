using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Port of lib/recorder.js: per-day jsonl recording, in-memory query,
/// self-contained HTML viewer regeneration, blind-box detection on gifts.
/// Default blind-box DB ships in Assets/blindbox-default.json; the runtime DB
/// (updated via /api/blindbox/save) lives in the data dir and wins when present.
/// </summary>
public sealed partial class Recorder
{
    public static readonly string[] Types = { "danmu", "gifts", "blindbox", "guard", "superchat" };

    private static readonly JsonSerializerOptions JsonLine = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Dictionary<string, string[]> Headers = new()
    {
        ["danmu"] = new[] { "时间", "用户名", "UID", "弹幕内容" },
        ["gifts"] = new[] { "时间", "用户名", "UID", "礼物名称", "数量", "单价(瓜子)", "总价(元)" },
        ["blindbox"] = new[] { "时间", "用户名", "UID", "盲盒名称", "数量", "成本(元)", "收入(元)", "盈亏(元)" },
        ["guard"] = new[] { "时间", "用户名", "UID", "舰长等级", "数量", "单价(瓜子)", "合计(元)" },
        ["superchat"] = new[] { "时间", "用户名", "UID", "留言内容", "价格(元)", "开始时间", "结束时间" },
    };

    private static readonly Dictionary<string, string[]> Columns = new()
    {
        ["danmu"] = new[] { "time", "uname", "uid", "msg" },
        ["gifts"] = new[] { "time", "uname", "uid", "giftName", "num", "price", "value" },
        ["blindbox"] = new[] { "time", "uname", "uid", "giftName", "num", "cost", "income", "profit" },
        ["guard"] = new[] { "time", "uname", "uid", "levelName", "num", "price", "value" },
        ["superchat"] = new[] { "time", "uname", "uid", "msg", "price", "startTime", "endTime" },
    };

    private static readonly Dictionary<string, string> Titles = new()
    {
        ["danmu"] = "弹幕记录", ["gifts"] = "礼物记录", ["blindbox"] = "盲盒记录",
        ["guard"] = "舰长记录", ["superchat"] = "醒目留言记录",
    };

    private readonly AppConfig _config;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, DayBucket>> _days = new();
    private readonly Dictionary<string, bool> _enabled = new();
    private JsonObject _blindBoxData = new();

    public Recorder(AppConfig config)
    {
        _config = config;
        // Original semantics: enabled unless recording[t] is explicitly false.
        var rec = config.GetNode("recording") as JsonObject;
        foreach (var t in Types) _enabled[t] = rec?.TryGetPropertyValue(t, out var v) == true && v is JsonValue val && val.TryGetValue<bool>(out var b) ? b : true;
        ReloadBlindBoxData();
        LoadExisting();
    }

    private string FolderOf(string type)
    {
        var folders = _config.GetNode("folders") as JsonObject;
        return folders?.TryGetPropertyValue(type, out var v) == true ? v?.GetValue<string>() ?? "" : "";
    }

    // ----- blind-box runtime DB -----

    public JsonObject BlindBoxData => _blindBoxData.DeepClone()!.AsObject();

    public void ReloadBlindBoxData()
    {
        try
        {
            var runtime = Path.Combine(AppConfig.DataDir, "blindbox-data.json");
            var path = File.Exists(runtime) ? runtime : Path.Combine(AppContext.BaseDirectory, "blindbox-default.json");
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
            {
                lock (_lock) _blindBoxData = o;
            }
        }
        catch { /* keep previous db */ }
    }

    public void SaveBlindBoxData(JsonObject data)
    {
        var obj = new JsonObject { ["_comment"] = "B站盲盒内置数据库 - 由检测按钮更新", ["_updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd") };
        foreach (var (k, v) in data)
            if (!k.StartsWith('_')) obj[k] = v?.DeepClone();
        Directory.CreateDirectory(AppConfig.DataDir);
        File.WriteAllText(Path.Combine(AppConfig.DataDir, "blindbox-data.json"), obj.ToJsonString());
        lock (_lock) _blindBoxData = obj;
    }

    // ----- recording -----

    public void SetEnabled(string type, bool on)
    {
        if (Types.Contains(type)) _enabled[type] = on;
    }

    public void HandleEvent(LiveEvent ev)
    {
        if (string.IsNullOrEmpty(ev.Type)) return;
        if (ev.Type == "gifts") MaybeBlindBox(ev);
        Record(ev.Type, ev);
    }

    private static readonly Regex BlindBoxKeyword = new("盲盒|魔盒|扭蛋|神秘|宝盒", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void MaybeBlindBox(LiveEvent gift)
    {
        var name = gift.GiftName ?? "";
        JsonObject? map = null;
        try { map = _config.GetNode("blindBox") as JsonObject; } catch { }
        var inMap = map?.ContainsKey(name) == true;
        JsonNode? builtIn = null;
        lock (_lock) _blindBoxData.TryGetPropertyValue(name, out builtIn);
        var builtInOk = builtIn != null && (builtIn?["expectIncome"] ?? 0).GetValue<double?>() != null;
        if (!inMap && !builtInOk && !BlindBoxKeyword.IsMatch(name)) return;

        double perIncome;
        if (inMap && map![name] is JsonValue mv && mv.TryGetValue<double>(out var mapVal)) perIncome = mapVal;
        else if (builtInOk && builtIn!["expectIncome"] is JsonValue bv) perIncome = bv.GetValue<double>();
        else perIncome = gift.Value;

        var income = Math.Round(perIncome * gift.Num * 100) / 100;
        var cost = Math.Round(gift.Value * 100) / 100;
        Record("blindbox", new LiveEvent
        {
            Type = "blindbox",
            Time = gift.Time.Length > 0 ? gift.Time : Now(),
            Ts = gift.Ts > 0 ? gift.Ts : DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            Uid = gift.Uid, Uname = gift.Uname, GiftName = name, Num = gift.Num,
            Cost = cost, Income = income, Profit = Math.Round((income - cost) * 100) / 100,
        });
    }

    public void Record(string type, LiveEvent ev)
    {
        if (!Types.Contains(type) || !_enabled.TryGetValue(type, out var on) || !on) return;
        var ts = ev.Ts > 0 ? ev.Ts : DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var date = DateKey(ts);
        lock (_lock)
        {
            var day = EnsureDayNoLock(type, date);
            day.Records.Add(ev);
            try
            {
                File.AppendAllText(day.JsonlPath, JsonSerializer.Serialize(ev, JsonLine) + "\n");
            }
            catch { /* transient IO issues must never break the live pipeline */ }
        }
    }

    // ----- query / files -----

    public object Query(string type, string? start, string? end, string? uid, string? q, int page, int size)
    {
        var list = new List<LiveEvent>();
        lock (_lock)
        {
            if (_days.TryGetValue(type, out var days))
                foreach (var day in days.Values)
                    list.AddRange(day.Records);
        }
        var startTs = ParseTimeInput(start);
        var endTs = ParseTimeInput(end);
        if (startTs != null) list.RemoveAll(r => (r.Ts == 0 ? long.MaxValue : r.Ts) < startTs);
        if (endTs != null) list.RemoveAll(r => (r.Ts == 0 ? 0 : r.Ts) > endTs);
        if (!string.IsNullOrEmpty(uid)) list.RemoveAll(r => r.Uid != uid);
        if (!string.IsNullOrEmpty(q))
        {
            var kq = q.ToLowerInvariant();
            list.RemoveAll(r => !(r.Uname + " " + r.Msg + " " + r.GiftName + " " + r.Uid).ToLowerInvariant().Contains(kq));
        }
        list.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        page = Math.Max(1, page);
        size = Math.Max(1, size);
        var rows = list.Skip((page - 1) * size).Take(size).Cast<object>().ToList();
        return new { total = list.Count, page, size, rows };
    }

    private sealed record FileEntry(string name, bool jsonl, bool html);

    public object ListFiles(string type)
    {
        var dir = FolderOf(type);
        var files = new List<FileEntry>();
        try
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".jsonl") || name.EndsWith(".html"))
                    files.Add(new FileEntry(name, name.EndsWith(".jsonl"), name.EndsWith(".html")));
            }
        }
        catch { }
        files.Sort((a, b) => string.CompareOrdinal(b.name, a.name));
        return new { dir, files };
    }

    public string GetFolder(string type) => FolderOf(type);

    public string OpenFolder(string type)
    {
        var dir = FolderOf(type);
        try { Directory.CreateDirectory(dir); } catch { }
        OpenInExplorer(dir);
        return dir;
    }

    public static void OpenInExplorer(string dir)
    {
        try
        {
            // Direct explorer invocation, same rationale as recorder.js (no shell quoting issues).
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ----- HTML viewer -----

    public string RegenerateHtml(string type, string date)
    {
        List<LiveEvent> records;
        DayBucket day;
        lock (_lock)
        {
            day = EnsureDayNoLock(type, date);
            records = new List<LiveEvent>(day.Records);
        }
        var tplPath = Path.Combine(AppContext.BaseDirectory, "viewer-template.html");
        if (!File.Exists(tplPath)) return tplPath;
        var tpl = File.ReadAllText(tplPath);
        var header = string.Join("", (Headers.GetValueOrDefault(type, Array.Empty<string>())).Select(h => "<th>" + h + "</th>"));
        var html = tpl
            .Replace("__HEADER__", header)
            .Replace("__TITLE__", (Titles.GetValueOrDefault(type, type)) + " · " + date)
            .Replace("__COLS__", JsonSerializer.Serialize(Columns.GetValueOrDefault(type, Array.Empty<string>())))
            .Replace("__DATA__", JsonSerializer.Serialize(records, JsonLine));
        File.WriteAllText(day.HtmlPath, html);
        return day.HtmlPath;
    }

    public void RegenerateToday(string type) => RegenerateHtml(type, DateKey(DateTimeOffset.Now.ToUnixTimeMilliseconds()));

    // ----- helpers -----

    private DayBucket EnsureDayNoLock(string type, string date)
    {
        if (!_days.TryGetValue(type, out var days)) _days[type] = days = new Dictionary<string, DayBucket>();
        if (days.TryGetValue(date, out var found)) return found;
        var dir = FolderOf(type);
        var bucket = new DayBucket(date, Path.Combine(dir, $"{type}-{date}.jsonl"), Path.Combine(dir, $"{type}-{date}.html"));
        try { Directory.CreateDirectory(dir); } catch { }
        days[date] = bucket;
        return bucket;
    }

    private void LoadExisting()
    {
        foreach (var type in Types)
        {
            var dir = FolderOf(type);
            string[] files = Array.Empty<string>();
            try { files = Directory.GetFiles(dir, "*.jsonl"); } catch { continue; }
            foreach (var f in files)
            {
                var m = DayFileRegex.Match(Path.GetFileName(f));
                if (!m.Success || m.Groups[1].Value != type) continue;
                LoadDayFile(type, m.Groups[2].Value, f);
            }
        }
    }

    private static readonly Regex DayFileRegex = new("^([a-z]+)-(\\d{4}-\\d{2}-\\d{2})\\.jsonl$", RegexOptions.Compiled);

    private void LoadDayFile(string type, string date, string jsonlPath)
    {
        var bucket = EnsureDayNoLock(type, date);
        try
        {
            foreach (var line in File.ReadLines(jsonlPath))
            {
                var s = line.Trim();
                if (s.Length == 0) continue;
                try
                {
                    var ev = JsonSerializer.Deserialize<LiveEvent>(s, JsonLine);
                    if (ev != null) bucket.Records.Add(ev);
                }
                catch { /* skip malformed lines like the JS version */ }
            }
        }
        catch { }
    }

    internal static string DateKey(long ts)
    {
        var d = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime;
        return d.ToString("yyyy-MM-dd");
    }

    internal static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static long? ParseTimeInput(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim().Replace('T', ' ');
        var m = TimeInputRegex.Match(v);
        if (!m.Success) return null;
        try
        {
            var dt = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0,
                m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : 0,
                m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 0);
            return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        }
        catch { return null; }
    }

    private static readonly Regex TimeInputRegex = new("^(\\d{4})-(\\d{2})-(\\d{2})(?: (\\d{2}):(\\d{2})(?::(\\d{2}))?)?$", RegexOptions.Compiled);

    private sealed class DayBucket
    {
        public DayBucket(string date, string jsonlPath, string htmlPath)
        {
            Date = date; JsonlPath = jsonlPath; HtmlPath = htmlPath;
        }

        public string Date { get; }
        public string JsonlPath { get; }
        public string HtmlPath { get; }
        public List<LiveEvent> Records { get; } = new();
    }
}
