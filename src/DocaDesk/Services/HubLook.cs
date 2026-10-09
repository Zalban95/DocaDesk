using System.Text.Json;
using DocaDesk.Core;
using DocaDesk.Core.Logging;
using DocaDesk.Core.Look;
using DocaDesk.Core.Models;

namespace DocaDesk.Services;

/// <summary>
/// The panel's look, read from the hub (<c>GET /api/v1/settings/look</c>, PROTOCOL §14.1) at each connection and again
/// on <c>settings.changed</c> with <c>look: true</c>, and handed to <see cref="LookApplier"/>. The last one is kept on
/// disk with the hub it came from, so a cold start draws it before the hub answers. DocaDesk's own look stays when the
/// device is not paired, when the hub is older (404), or when the person switches it off (Settings → General).
/// </summary>
public sealed class HubLook
{
    private readonly AppSession? _session;
    private readonly IDocaLogger _log = new RedactingLogger();
    private readonly object _gate = new();
    private PanelLook? _look;
    private string? _lookServer;
    private SessionState _lastState = SessionState.Unpaired;
    private string? _published;
    private static HashSet<string>? _installed;

    /// <summary>The look to draw now (null: DocaDesk's own). Raised on any thread.</summary>
    public event Action<DeskLook?>? Changed;

    public DeskLook? Current { get; private set; }

    /// <summary>What the panel's look is, in words, for Settings.</summary>
    public string Describe()
    {
        if (!AppPrefs.UseHubLook) return "Now: DocaDesk's own look.";
        if (Current is null) return _session?.State == SessionState.Paired
            ? "Now: DocaDesk's own look — the hub does not say its look (older than 2.342)."
            : "Now: DocaDesk's own look — not connected to a hub.";
        return $"Now: {Current.Label ?? "the panel's look"}, from the hub ({(Current.Dark ? "dark" : "light")}).";
    }

    public HubLook(AppSession? session)
    {
        _session = session;
        if (session is null) return;
        session.Changed += OnSessionChanged;
        session.EventReceived += (ev, ct) =>
        {
            if (PanelLookWire.IsLookChange(ev)) _ = RefreshAsync();
            return Task.CompletedTask;
        };
        LoadCache();
    }

    /// <summary>A look given directly — the preview (<c>--look-preview file.json</c>), which has no hub.</summary>
    public void Use(PanelLook? look)
    {
        lock (_gate) _look = look;
        Publish(force: true);
    }

    /// <summary>The person turned "Use the hub's look" on or off.</summary>
    public void SetEnabled(bool on)
    {
        AppPrefs.UseHubLook = on;
        Publish(force: true);
        if (on && _look is null && _session?.State == SessionState.Paired) _ = RefreshAsync();
    }

    private void OnSessionChanged()
    {
        if (_session is null) return;
        var state = _session.State;
        var was = _lastState;
        _lastState = state;
        switch (state)
        {
            case SessionState.Paired when was != SessionState.Paired:
                if (_look is not null && !SameServer(_lookServer)) { lock (_gate) _look = null; Publish(); }
                else Publish();   // the cached one, before the hub answers
                _ = RefreshAsync();
                break;
            case SessionState.Unpaired or SessionState.Revoked when was is SessionState.Paired or SessionState.Offline:
                lock (_gate) { _look = null; _lookServer = null; }
                DeleteCache();
                Publish();
                break;
            case SessionState.Offline:
                Publish();   // keep the last look while the hub is away
                break;
        }
    }

    /// <summary>Read the look from the hub. A failure keeps the last one; a 404 (an older hub) is DocaDesk's own.</summary>
    public async Task RefreshAsync()
    {
        var client = _session?.Client;
        if (client is null || string.IsNullOrEmpty(client.Token)) return;
        try
        {
            var look = await client.GetLookAsync().ConfigureAwait(false);
            lock (_gate) { _look = look; _lookServer = _session!.ServerUrl; }
            if (look is null) DeleteCache(); else SaveCache(look);
            Publish();
        }
        catch (Exception ex)
        {
            _log.Warn("Reading the hub's look failed: " + ex.Message);
        }
    }

    private void Publish(bool force = false)
    {
        PanelLook? look;
        lock (_gate) look = _look;
        var desk = AppPrefs.UseHubLook ? DeskLook.From(look, IsInstalled) : null;
        var key = desk is null ? "" : desk.Etag + (desk.Dark ? "d" : "l");
        if (!force && key == _published) return;
        _published = key;
        Current = desk;
        try { Changed?.Invoke(desk); }
        catch (Exception ex) { _log.Warn("Applying the look failed: " + ex.Message); }
    }

    private bool SameServer(string? server) =>
        server is not null && _session is not null &&
        string.Equals(server.TrimEnd('/'), _session.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether Windows has a font family (read once).</summary>
    private static bool IsInstalled(string family)
    {
        if (_installed is null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var fonts = new System.Drawing.Text.InstalledFontCollection();
                foreach (var f in fonts.Families) set.Add(f.Name);
            }
            catch { /* no list: only what the app carries and the generic names */ }
            _installed = set;
        }
        return _installed.Contains(family);
    }

    // The last look, with the hub it came from: %LOCALAPPDATA%\DocaDesk\look.json. Colours and fonts only — nothing
    // in it is a secret.
    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocaDesk", "look.json");

    private sealed class Cached
    {
        public string? Server { get; set; }
        public PanelLook? Look { get; set; }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var c = DocaJson.Deserialize<Cached>(File.ReadAllText(CachePath));
            if (c?.Look is null) return;
            lock (_gate) { _look = c.Look; _lookServer = c.Server; }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    private void SaveCache(PanelLook look)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, DocaJson.Serialize(new Cached { Server = _session?.ServerUrl, Look = look }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteCache()
    {
        try { if (File.Exists(CachePath)) File.Delete(CachePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
