using Microsoft.Win32;

namespace Wallerpaper.Shell.Services;

public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BaiAcha";

    /// <summary>Moves the pre-1.0 "WallerpaperShell" Run entry to "BaiAcha" so
    /// an upgraded install keeps starting with Windows.</summary>
    public static void MigrateLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (key.GetValue(ValueName) is string current && !string.IsNullOrWhiteSpace(current))
                return;
            if (key.GetValue("WallerpaperShell") is not string)
                return;
            var exe = Environment.ProcessPath;
            key.DeleteValue("WallerpaperShell", throwOnMissingValue: false);
            if (string.IsNullOrWhiteSpace(exe))
                return;
            key.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        catch
        {
            // Best effort: the settings page can re-enable autostart.
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                throw new InvalidOperationException("Cannot resolve process path.");
            // HKCU Run, no elevation. --minimized starts directly in tray.
            key.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
