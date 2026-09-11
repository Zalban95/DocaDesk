using Microsoft.Win32;

namespace DocaDesk.Services;

/// <summary>Per-user HKCU Run key. Only written when the user opts in.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DocaDesk";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot resolve process path for autostart.");
            // Start to tray: app hides until activated; Program can check --tray later.
            key.SetValue(ValueName, $"\"{exe}\" --tray");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
