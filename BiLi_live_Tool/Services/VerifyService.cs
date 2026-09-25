// VerifyService.cs — white-list authorization (port of Bin/lib/verify.js +
// the verify-lock flow in Bin/src/main.js).
//
// Behaviour parity with the Electron original:
//   - uid comes from the B站 cookie (DedeUserID), signature = HMAC-SHA256 of
//     "uid + ts" with the shared secret, sent to the author's verify endpoint;
//   - a cookie appearing/changing (i.e. the user logged in) triggers a check;
//   - on failure the app locks: connecting to a room is refused and an existing
//     connection is dropped — every other page stays usable, with the panel
//     explaining how to get authorized;
//   - codes/messages mirror the original table (NO_UID / TS_EXPIRED / BAD_SIGN /
//     NOT_WHITELISTED / EXPIRED / TIMEOUT / NETWORK_ERROR).
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BiLi_live_Tool.Services;

public sealed record VerifyState(
    bool Locked,
    long Uid,
    string Name,
    string Code,
    string Message,
    string CheckedAt,
    bool Checked);

public sealed record VerifyRefreshResult(
    bool Ok,
    bool RateLimited,
    int CooldownRemaining,
    int Failures,
    string Message,
    VerifyState State);

public sealed class VerifyService
{
    public const string AuthorUid = "10412378";
    private const string VerifyUrl = "https://ai-daynews.xyz/api/verify";
    /// <summary>
    /// HMAC key for the whitelist endpoint. Deliberately kept OUT of the repository:
    /// it is read from %BLT_VERIFY_KEY% or &lt;app&gt; erify-key.txt (copied into
    /// builds that legitimately talk to the author's verify server). When missing we
    /// report a clear reason instead of letting the server answer "签名验证失败".
    /// </summary>
    private static readonly string HmacSecret = LoadSecret();

    private static string LoadSecret()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("BLT_VERIFY_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            var file = System.IO.Path.Combine(AppContext.BaseDirectory, "verify-key.txt");
            if (System.IO.File.Exists(file))
            {
                var text = System.IO.File.ReadAllText(file).Trim();
                if (text.Length > 0) return text;
            }
        }
        catch { }
        return "";
    }
    private static readonly Regex DedeUserIDRegex = new(@"(?:^|;\s*)DedeUserID=(\d+)", RegexOptions.Compiled);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(9) };

    private sealed class UidThrottleState
    {
        public int ConsecutiveFailures;
        public DateTimeOffset CooldownUntil;
    }

    private readonly ConcurrentDictionary<long, UidThrottleState> _throttles = new();
    private readonly ConcurrentDictionary<long, Task<(bool Ok, string Name, string Code, string Message)>> _inFlight = new();

    private readonly AppConfig _config;
    private readonly LiveService _live;
    private readonly object _lock = new();
    private VerifyState _state = new(false, 0, "", "", "", "", false);
    private long _lastUid;
    /// <summary>Diagnostics only: force the lock so the gated UI can be exercised.</summary>
    private bool? _debugLock;      // uid of the cookie we already verified
    private DateTimeOffset _lastAttempt;

    public VerifyService(AppConfig config, LiveService live)
    {
        _config = config;
        _live = live;
    }

    public VerifyState State { get { lock (_lock) return _state; } }

    public bool Locked => _debugLock ?? State.Locked;

    /// <summary>Current B站 UID from the active cookie.</summary>
    public long CurrentUid => ExtractUid(_config.Cookie);

    /// <summary>Remaining cooldown seconds for a specific UID (0 if ready).</summary>
    public int GetCooldownRemaining(long uid)
    {
        if (uid <= 0) return 0;
        if (_throttles.TryGetValue(uid, out var s))
        {
            var remaining = (s.CooldownUntil - DateTimeOffset.UtcNow).TotalSeconds;
            if (remaining > 0) return (int)Math.Ceiling(remaining);
        }
        return 0;
    }

    /// <summary>Consecutive verification failure count for a specific UID.</summary>
    public int GetConsecutiveFailures(long uid)
    {
        if (uid <= 0) return 0;
        return _throttles.TryGetValue(uid, out var s) ? s.ConsecutiveFailures : 0;
    }

    public int CurrentCooldownRemaining => GetCooldownRemaining(CurrentUid);
    public int CurrentConsecutiveFailures => GetConsecutiveFailures(CurrentUid);

    /// <summary>Diagnostics hook (loopback bridge only): force/clear the locked state.</summary>
    public void SetDebugLock(bool? locked)
    {
        _debugLock = locked;
        Changed?.Invoke();
    }

    public event Action? Changed;

    /// <summary>UID from a B站 cookie (0 when absent) — identical regex to verify.js.</summary>
    public static long ExtractUid(string? cookie)
    {
        if (string.IsNullOrEmpty(cookie)) return 0;
        var m = DedeUserIDRegex.Match(cookie);
        return m.Success && long.TryParse(m.Groups[1].Value, out var uid) ? uid : 0;
    }

    private static string Sign(long uid, string ts)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(HmacSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(uid + ts));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>
    /// Checks the current cookie's uid unless it was already verified.
    /// Backward-compatible wrapper over RefreshDetailedAsync.
    /// </summary>
    public async Task<VerifyState> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        var res = await RefreshDetailedAsync(force, ct).ConfigureAwait(false);
        return res.State;
    }

    /// <summary>
    /// Checks the current cookie's uid with detailed rate limiting and result information.
    /// Employs per-UID rate limiting and in-flight request collapsing.
    /// </summary>
    public async Task<VerifyRefreshResult> RefreshDetailedAsync(bool force = false, CancellationToken ct = default)
    {
        var uid = ExtractUid(_config.Cookie);
        if (uid == 0)
        {
            _lastUid = 0;
            SetState(new VerifyState(true, 0, "", "NO_UID", "未检测到 B站 UID：请先登录 B站账号（未授权账号登出后同样会锁定）", Now(), true));
            _live.Stop();
            return new VerifyRefreshResult(false, false, 0, 0, State.Message, State);
        }

        if (force)
        {
            var cooldown = GetCooldownRemaining(uid);
            if (cooldown > 0)
            {
                return new VerifyRefreshResult(
                    Ok: false,
                    RateLimited: true,
                    CooldownRemaining: cooldown,
                    Failures: GetConsecutiveFailures(uid),
                    Message: $"重试过于频繁，请等待 {cooldown} 秒后再试",
                    State: State);
            }
        }
        else
        {
            if (uid == _lastUid && State.Checked)
                return new VerifyRefreshResult(!State.Locked, false, 0, GetConsecutiveFailures(uid), State.Message, State);
            if (DateTimeOffset.UtcNow - _lastAttempt < TimeSpan.FromSeconds(10))
                return new VerifyRefreshResult(!State.Locked, false, 0, GetConsecutiveFailures(uid), State.Message, State);
        }

        _lastAttempt = DateTimeOffset.UtcNow;

        if (HmacSecret.Length == 0)
        {
            _lastUid = uid;
            SetState(new VerifyState(true, uid, "", "NO_KEY",
                "授权校验密钥缺失（源码不含密钥）：自行构建请联系作者获取 verify-key.txt", Now(), true));
            return new VerifyRefreshResult(false, false, 0, 0, State.Message, State);
        }

        // Per-UID request collapsing: if multiple callers trigger verify for the same UID simultaneously,
        // they all share the exact same in-flight task instead of sending duplicate network packets.
        var task = _inFlight.GetOrAdd(uid, id => PerformVerifyUidAsync(id, ct));
        var (ok, name, code, message) = await task.ConfigureAwait(false);

        if (ok)
        {
            _lastUid = uid;
            _throttles.TryRemove(uid, out _);
            SetState(new VerifyState(false, uid, name, "", "已授权", Now(), true));
            return new VerifyRefreshResult(true, false, 0, 0, "验证通过：" + (string.IsNullOrEmpty(name) ? uid.ToString() : name), State);
        }
        else
        {
            _lastUid = uid;
            var softFailure = code is "NETWORK_ERROR" or "TIMEOUT";
            SetState(new VerifyState(
                softFailure ? State.Locked : true,
                uid, "", code,
                softFailure ? message + "（网络问题，稍后自动重试，不影响本地功能）" : message,
                Now(), true));

            // Record failure and calculate progressive backoff cooldown for this specific UID
            var throttle = _throttles.GetOrAdd(uid, _ => new UidThrottleState());
            int cooldownSec;
            lock (throttle)
            {
                throttle.ConsecutiveFailures++;
                cooldownSec = throttle.ConsecutiveFailures switch
                {
                    1 => 10,
                    2 => 20,
                    _ => 40
                };
                throttle.CooldownUntil = DateTimeOffset.UtcNow.AddSeconds(cooldownSec);
            }

            if (State.Locked) _live.Stop();

            return new VerifyRefreshResult(
                Ok: false,
                RateLimited: false,
                CooldownRemaining: cooldownSec,
                Failures: throttle.ConsecutiveFailures,
                Message: State.Message,
                State: State);
        }
    }

    private async Task<(bool Ok, string Name, string Code, string Message)> PerformVerifyUidAsync(long uid, CancellationToken ct)
    {
        try
        {
            return await VerifyUidAsync(uid, ct).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(uid, out _);
        }
    }

    /// <summary>Forgets the verified uid so the next RefreshAsync re-checks (new login).</summary>
    public void Reset()
    {
        _lastUid = 0;
        SetState(new VerifyState(false, 0, "", "", "", Now(), false));
    }

    private async Task<(bool Ok, string Name, string Code, string Message)> VerifyUidAsync(long uid, CancellationToken ct)
    {
        try
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var url = $"{VerifyUrl}?uid={uid}&ts={ts}&sign={Sign(uid, ts)}";
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True)
            {
                var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? "" : "";
                return (true, name, "", "");
            }
            var code = root.ValueKind == JsonValueKind.Object
                       && root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "UNKNOWN" : "UNKNOWN";
            return (false, "", code, MessageForCode(code));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return (false, "", "TIMEOUT", "验证服务连接超时，请检查网络");
        }
        catch (Exception ex)
        {
            return (false, "", "NETWORK_ERROR", "无法连接验证服务: " + ex.Message);
        }
    }

    private static string MessageForCode(string code) => code switch
    {
        "MISSING_PARAMS" => "验证参数缺失",
        "TS_EXPIRED" => "请求已过期，请检查系统时间",
        "BAD_SIGN" => "签名验证失败",
        "NOT_WHITELISTED" => "你的 B站账号不在授权名单中",
        "EXPIRED" => "你的授权已过期",
        _ => "验证失败 (" + code + ")",
    };

    private void SetState(VerifyState next)
    {
        bool changed;
        lock (_lock)
        {
            changed = _state.Locked != next.Locked || _state.Code != next.Code
                      || _state.Message != next.Message || _state.Uid != next.Uid
                      || _state.Checked != next.Checked || _state.Name != next.Name;
            _state = next;
        }
        if (changed) Changed?.Invoke();
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
}
