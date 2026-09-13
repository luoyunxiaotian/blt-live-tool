// AutoLaunchService.cs — launch-at-login toggle via the per-user HKCU Run key.
// Ported (rewritten, not copied) from the Electron original Bin/src/auto-launch.js,
// which called Electron's app.setLoginItemSettings; on Windows that API writes the
// same HKCU\Software\Microsoft\Windows\CurrentVersion\Run value this service manages.
//
// Notes:
//   - Value data is the current exe path plus the "/autostart" flag. The path is
//     double-quoted because the Run command line is split on spaces (the default
//     build output path contains spaces, e.g. ".../MAUI_Blazor Hybrid/...").
//   - Only HKCU is touched: no elevation required, per-user setting.

#if WINDOWS
using Microsoft.Win32;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Reads/writes the HKCU Run registry value that starts the app at Windows login.
/// </summary>
public static class AutoLaunchService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BiLi_live_Tool";

    /// <summary>
    /// Returns true when the "BiLi_live_Tool" value exists under the HKCU Run key
    /// (i.e. auto-launch is currently enabled). Returns false on any registry error.
    /// </summary>
    public static bool Get()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            // A missing/inaccessible Run key simply means "not registered".
            return false;
        }
    }

    /// <summary>
    /// Enables (writes exe path + " /autostart") or disables (deletes) the Run value.
    /// Failures are swallowed: auto-launch is a convenience and must not break startup.
    /// </summary>
    public static void Set(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    return; // No exe path (e.g. exotic host); leave the value untouched.

                key.SetValue(ValueName, $"\"{exePath}\" /autostart");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry write failures (policy, AV, etc.) are non-fatal.
        }
    }
}
#else
namespace BiLi_live_Tool.Services;

/// <summary>
/// Non-Windows stub. Same public surface as the Windows implementation so shared
/// wiring code compiles for every target framework; all members are no-ops.
/// </summary>
public static class AutoLaunchService
{
    public static bool Get()
    {
        // Not supported on non-Windows targets.
        return false;
    }

    public static void Set(bool enabled)
    {
        // Not supported on non-Windows targets.
    }
}
#endif
