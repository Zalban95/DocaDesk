using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocaDesk.Core.Audit;

namespace DocaDesk.Mcp;

public interface IMcpTool
{
    string Name { get; }
    string Description { get; }
    bool ReadOnlyHint { get; }
    JsonObject InputSchema { get; }
    Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct);
}

public sealed class McpToolResult
{
    public bool IsError { get; init; }
    public string Text { get; init; } = "";
    /// <summary>A picture to send as an MCP image part beside the text (a screenshot), so the agent sees it — a media id
    /// alone reached no agent tool (hub audit 2026-10-06, cl 3).</summary>
    public byte[]? ImageBytes { get; init; }
    public string ImageMime { get; init; } = "image/png";

    /// <summary>The MCP content array for this result.</summary>
    public object[] Content() => ImageBytes is { Length: > 0 }
        ? new object[] { new { type = "text", text = Text }, new { type = "image", data = Convert.ToBase64String(ImageBytes), mimeType = ImageMime } }
        : new object[] { new { type = "text", text = Text } };
}

/// <summary>
/// The consent gate, read from every connection thread and written from the UI.
///
/// This was a plain Dictionary. `IsEnabled` runs on a listener thread for every
/// tools/call while `Register` inserts a server's tools from whichever thread
/// started it and `Set` writes from the UI — so ticking "Allow its tools" during
/// a call could be answered from a dictionary mid-resize: a wrong verdict in
/// either direction, which on a consent gate is the direction that matters, or a
/// lookup spinning in a broken bucket chain. `Snapshot` enumerating during an
/// insert threw straight out of the UI refresh.
/// </summary>
public sealed class ToolConsent
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _enabled = new(StringComparer.Ordinal)
    {
        ["list_windows"] = false,
        ["screenshot"] = false,
        ["get_clipboard_text"] = false,
        ["set_clipboard_text"] = false,
        ["open_url"] = false,
    };

    public bool IsEnabled(string name) => _enabled.TryGetValue(name, out var v) && v;
    public void Set(string name, bool on) { if (_enabled.ContainsKey(name)) _enabled[name] = on; }
    public IReadOnlyDictionary<string, bool> Snapshot() => new Dictionary<string, bool>(_enabled);

    /// <summary>
    /// Announce a tool that did not exist when this was built — the tools of a
    /// local MCP server appear when it starts. <see cref="Set"/> ignores an
    /// unknown name on purpose, so a dynamic tool has to be registered before
    /// the user can flip it.
    /// </summary>
    public void Register(string name, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _enabled[name] = enabled;
    }

    public void Remove(string name) => _enabled.TryRemove(name, out _);
}

public sealed class McpListenerOptions
{
    public required string PathSecret { get; set; }
    public string? BindAddress { get; set; }
    public int Port { get; set; } = 8742;
    public string? AllowedRemoteHost { get; set; }
    public bool AllowLoopback { get; set; }
    /// <summary>When set and <see cref="EnforceBearer"/> is true, require Authorization: Bearer …</summary>
    public string? RequiredBearerToken { get; set; }
    /// <summary>False until the host is known to send the header (addendum §3.4).</summary>
    public bool EnforceBearer { get; set; }
    public ToolConsent Consent { get; set; } = new();
    public AuditLog? Audit { get; set; }
    public IReadOnlyList<IMcpTool> Tools { get; set; } = Array.Empty<IMcpTool>();
    /// <summary>
    /// Tools that come and go — the ones a local MCP server contributes while it
    /// is running. Asked on every list and every call, because a stopped server
    /// has to stop appearing.
    /// </summary>
    public Func<IReadOnlyList<IMcpTool>>? DynamicTools { get; set; }
    /// <summary>A tool that never answers must not hold the request open forever.</summary>
    public TimeSpan ToolCallTimeout { get; set; } = TimeSpan.FromSeconds(120);
}

/// <summary>
/// Hand-rolled HTTP MCP server matching Doca's client: POST JSON-RPC → JSON.
/// Tailscale bind only; secret path; remote-address check.
/// </summary>
public sealed class McpHttpListener : IAsyncDisposable
{
    private readonly McpListenerOptions _opt;
    private readonly McpDispatcher _dispatch;
    private SimpleHttpServer? _server;

    public bool IsRunning => _server?.IsRunning == true;
    public string? BoundUrl { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Flip bearer enforcement on a live listener once the host has the header.</summary>
    public void SetEnforceBearer(bool enforce) => _opt.EnforceBearer = enforce;

    public bool EnforceBearer => _opt.EnforceBearer;

    public McpHttpListener(McpListenerOptions options)
    {
        _opt = options;
        _dispatch = new McpDispatcher(options);
    }

    public static string NewPathSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string? FindTailscaleIpv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            var name = nic.Name + " " + nic.Description;
            var looksTs = name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address.ToString();
                if (ip.StartsWith("100.", StringComparison.Ordinal) || looksTs)
                    return ip;
            }
        }
        return null;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        LastError = null;

        var bind = _opt.BindAddress ?? FindTailscaleIpv4();
        if (string.IsNullOrEmpty(bind))
        {
            LastError = "No Tailscale IPv4 found. Connect Tailscale before starting the MCP listener.";
            throw new InvalidOperationException(LastError);
        }

        if (bind is "0.0.0.0" or "::")
            throw new InvalidOperationException("Refusing to bind MCP to a wildcard address.");

        if (!IPAddress.TryParse(bind, out var ip))
            throw new InvalidOperationException("Invalid bind address.");

        _server = new SimpleHttpServer(ip, _opt.Port, HandleAsync);
        _server.Start();
        BoundUrl = $"http://{bind}:{_opt.Port}/mcp/{_opt.PathSecret}";
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Test helper: bind loopback (not for production).</summary>
    public async Task StartLoopbackForTestsAsync(int port, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        _opt.BindAddress = "127.0.0.1";
        _opt.Port = port;
        _opt.AllowLoopback = true;
        _server = new SimpleHttpServer(IPAddress.Loopback, port, HandleAsync);
        _server.Start();
        BoundUrl = $"http://127.0.0.1:{port}/mcp/{_opt.PathSecret}";
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
        BoundUrl = null;
    }

    private async Task<HttpResponse> HandleAsync(HttpRequest req)
    {
        if (!IsRemoteAllowed(req.Remote))
            return new HttpResponse(403, "text/plain", "forbidden remote");

        var expected = "/mcp/" + _opt.PathSecret;
        if (!string.Equals(req.Path, expected, StringComparison.Ordinal))
            return new HttpResponse(404, "text/plain", "not found");

        if (_opt.EnforceBearer && !string.IsNullOrEmpty(_opt.RequiredBearerToken))
        {
            if (!req.Headers.TryGetValue("Authorization", out var auth) ||
                !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(auth["Bearer ".Length..].Trim(), _opt.RequiredBearerToken, StringComparison.Ordinal))
            {
                // Same shape as a wrong path — do not confirm the path was right.
                return new HttpResponse(404, "text/plain", "not found");
            }
        }

        // Streamable HTTP's other half: the GET stream DOCA holds open (modules/mcp/client.js
        // `_listen`, DOCA 2.90.0+) to hear notifications/tools/list_changed. Same gates as a POST.
        if (string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            req.Headers.TryGetValue("Accept", out var accept) && accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return new HttpResponse(200, "text/event-stream", "", ServeEventsAsync);

        if (!string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return new HttpResponse(405, "text/plain", "POST only");

        var json = await _dispatch.DispatchAsync(req.Body, "http").ConfigureAwait(false);
        return new HttpResponse(200, "application/json", json);
    }

    /* ── Notifications: the GET event stream ─────────────── */

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Threading.Channels.Channel<string>> _streams = new();
    private int _streamSeq;

    /// <summary>How many event streams are open now (a client listening for tool changes).</summary>
    public int OpenStreams => _streams.Count;

    /// <summary>How often an idle stream carries a comment, so proxies and the far end know it is alive.</summary>
    public TimeSpan StreamHeartbeat { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Tell every listening client that the tool list changed — a local server started, stopped,
    /// or had its consent changed. DOCA answers by calling tools/list again, so a tool that appears
    /// later is seen without anyone pressing ↺ Tools.
    /// </summary>
    public void NotifyToolsChanged()
    {
        const string frame = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}\n\n";
        foreach (var ch in _streams.Values) ch.Writer.TryWrite(frame);
    }

    private async Task ServeEventsAsync(Stream stream, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _streamSeq);
        var ch = System.Threading.Channels.Channel.CreateUnbounded<string>();
        _streams[id] = ch;
        try
        {
            await WriteFrameAsync(stream, ": open\n\n", ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(StreamHeartbeat);
                string frame;
                try { frame = await ch.Reader.ReadAsync(wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { frame = ": ping\n\n"; }
                await WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);   // throws when the far end has gone
            }
        }
        catch { /* the client went away, or the listener stopped */ }
        finally { _streams.TryRemove(id, out _); }
    }

    private static async Task WriteFrameAsync(Stream stream, string frame, CancellationToken ct)
    {
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(frame), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private bool IsRemoteAllowed(IPAddress? remote)
    {
        if (remote is null) return false;
        if (IPAddress.IsLoopback(remote))
            return _opt.AllowLoopback;

        if (!string.IsNullOrEmpty(_opt.AllowedRemoteHost))
        {
            try
            {
                var addrs = Dns.GetHostAddresses(_opt.AllowedRemoteHost);
                if (addrs.Any(a => a.Equals(remote)))
                    return true;
            }
            catch { /* ignore */ }
        }

        return remote.ToString().StartsWith("100.", StringComparison.Ordinal);
    }

    /// <summary>Tools whose results are other people's words (kept here for its callers; the set is the dispatcher's).</summary>
    public static HashSet<string> OpenWorld => McpDispatcher.OpenWorld;

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
