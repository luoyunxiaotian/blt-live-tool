namespace BiLi_live_Tool.Services;

/// <summary>
/// Turns "save on every change" into one debounced write per key. Pages call
/// <see cref="Run"/> from any field change handler — typing or dragging a
/// slider then produces a single config write instead of one per keystroke,
/// which is what makes auto-save affordable everywhere.
/// </summary>
public sealed class DebouncedSaver
{
    private readonly Dictionary<string, CancellationTokenSource> _pending = new();
    private readonly object _lock = new();

    /// <summary>
    /// Runs <paramref name="save"/> once the key has been quiet for
    /// <paramref name="delayMs"/>; a newer call for the same key wins.
    /// The delegate is invoked on a background thread, so callers that touch
    /// component state must marshal back (e.g. <c>() =&gt; InvokeAsync(SaveAsync)</c>).
    /// </summary>
    public void Run(string key, Func<Task> save, int delayMs = 450)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_pending.TryGetValue(key, out var prev))
            {
                try { prev.Cancel(); } catch { }
                prev.Dispose();
            }
            cts = new CancellationTokenSource();
            _pending[key] = cts;
        }
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs, token); }
            catch (TaskCanceledException) { return; }
            if (token.IsCancellationRequested) return;
            try { await save(); }
            catch { }
            lock (_lock)
            {
                if (_pending.TryGetValue(key, out var cur) && cur.Token == token)
                {
                    _pending.Remove(key);
                    cur.Dispose();
                }
            }
        });
    }

    /// <summary>Fires immediately (used by "save now" affordances).</summary>
    public void RunNow(string key, Func<Task> save) => Run(key, save, 0);
}
