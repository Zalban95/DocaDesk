using DocaDesk.Core;
using DocaDesk.Core.Look;

namespace DocaDesk.Services;

/// <summary>
/// <c>DocaDesk.exe --look-preview look.json</c>: the Settings window drawn in a look read from a file — a
/// <c>GET /api/v1/settings/look</c> body, or the bare look — and drawn again whenever the file changes. For seeing a
/// look (and taking its screenshots) without a hub and without touching the DocaDesk a person runs: it is its own
/// instance, with no session, tray or listener, and changes nothing unless a switch in it is flipped.
/// </summary>
public static class LookPreview
{
    private static FileSystemWatcher? _watcher;

    public static string? PathFrom(IEnumerable<string> args)
    {
        var list = args.ToList();
        var i = list.FindIndex(a => string.Equals(a, "--look-preview", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < list.Count ? Path.GetFullPath(list[i + 1]) : null;
    }

    /// <summary><c>--section desk</c>: which Settings section the preview opens on (general, desk, hands, servers, activity).</summary>
    public static string? SectionFrom(IEnumerable<string> args)
    {
        var list = args.ToList();
        var i = list.FindIndex(a => string.Equals(a, "--section", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < list.Count ? list[i + 1] : null;
    }

    public static void Start(string path, HubLook look)
    {
        void Load()
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { return; }   // still being written; the next change reads it
            look.Use(PanelLookWire.Parse(text) ?? Bare(text));
        }
        Load();
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => { Thread.Sleep(150); Load(); };
        _watcher.Created += (_, _) => { Thread.Sleep(150); Load(); };
    }

    private static PanelLook? Bare(string json)
    {
        try { return DocaJson.Deserialize<PanelLook>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
