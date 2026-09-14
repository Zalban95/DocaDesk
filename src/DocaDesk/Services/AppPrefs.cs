using Microsoft.Win32;

namespace DocaDesk.Services;

/// <summary>Small HKCU prefs for app-local settings (not the server profile).</summary>
public static class AppPrefs
{
    private const string KeyPath = @"Software\DocaDesk";

    public static bool StartMinimized
    {
        get => ReadBool("StartMinimized", false);
        set => WriteBool("StartMinimized", value);
    }

    public static bool NotifyPrompts
    {
        get => ReadBool("NotifyPrompts", true);
        set => WriteBool("NotifyPrompts", value);
    }

    public static bool NotifyAlerts
    {
        get => ReadBool("NotifyAlerts", true);
        set => WriteBool("NotifyAlerts", value);
    }

    /// <summary>When true, start the MCP listener after a successful pair (still requires per-tool consent).</summary>
    public static bool McpAutoStart
    {
        get => ReadBool("McpAutoStart", false);
        set => WriteBool("McpAutoStart", value);
    }

    /// <summary>When true, the window X hides to the tray. Default is false: X quits.</summary>
    public static bool CloseToTray
    {
        get => ReadBool("CloseToTray", false);
        set => WriteBool("CloseToTray", value);
    }

    public static bool GetToolConsent(string tool, bool fallback = false) => ReadBool("Tool." + tool, fallback);

    public static void SetToolConsent(string tool, bool enabled) => WriteBool("Tool." + tool, enabled);

    private static bool ReadBool(string name, bool fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
        var v = key?.GetValue(name);
        if (v is int i) return i != 0;
        if (v is string s && bool.TryParse(s, out var b)) return b;
        return fallback;
    }

    private static void WriteBool(string name, bool value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }
}
