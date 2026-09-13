using System.Threading;

namespace BiLi_live_Tool.Services;

#if !WINDOWS
/// <summary>Stub for non-Windows TFMs.</summary>
public static class SingleInstance
{
    public static void EnforceOrExit() { }
}
#else
/// <summary>
/// Single-instance enforcement (port of requestSingleInstanceLock): first
/// launch owns a named mutex and watches a named auto-reset event; further
/// launches signal the event (first instance restores its window) and exit.
/// </summary>
public static class SingleInstance
{
    // Names are scoped to the installation folder: two copies (e.g. a development
    // build and a packaged build) must be able to run side by side, while two
    // launches of the *same* copy still collapse into one instance.
    private static readonly string MutexName = @"Local\BiLi_live_Tool_MAUI_SingleInstance_" + FolderKey();
    private static readonly string ShowEventName = @"Local\BiLi_live_Tool_MAUI_Show_" + FolderKey();

    private static string FolderKey()
    {
        try
        {
            var dir = (AppContext.BaseDirectory ?? "").TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).ToLowerInvariant();
            using var sha = System.Security.Cryptography.SHA1.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(dir));
            return Convert.ToHexString(hash, 0, 6);
        }
        catch { return "default"; }
    }

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    public static void EnforceOrExit()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (createdNew)
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _ = Task.Run(() =>
            {
                while (_showEvent.WaitOne())
                {
                    // Restores (never minimizes) — this path only fires when a
                    // second launch asks the first one to come to front.
                    App.RestoreMainWindow();
                }
            });
            return;
        }

        // A previous instance's abandoned mutex (crash) still means "exists".
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
        }
        catch { }
        Environment.Exit(0);
    }
}
#endif
