using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using DocaDesk.Core.Audit;

namespace DocaDesk.Mcp;

public sealed class McpSocketOptions
{
    /// <summary><c>wss://&lt;hub&gt;/api/v1/mcp/host</c> (<c>ws://</c> only in tests).</summary>
    public required Uri Endpoint { get; init; }
    /// <summary>The device token, asked at every dial (it can change with a new pairing).</summary>
    public required Func<string?> Token { get; init; }
    /// <summary>The hub's certificate trust — the pin taken at pairing, else the CAs (DocaClient's policy).</summary>
    public Func<X509Certificate?, X509Chain?, SslPolicyErrors, bool>? TrustCertificate { get; init; }
    public AuditLog? Audit { get; init; }
    /// <summary>A message this often keeps proxies from closing an idle socket (PROTOCOL.md §22.2: every 20–30 s).</summary>
    public TimeSpan Keepalive { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan FirstRetry { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetry { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The MCP server on a socket this PC opens to the hub (PROTOCOL.md §22.2; the hub's side is
/// modules/mcp/socket-hosts.js, the browser extension the first such client). Instead of the hub dialling a listener on
/// the tailnet, DocaDesk dials <c>wss://&lt;hub&gt;/api/v1/mcp/host</c> with its token and answers the hub's JSON-RPC
/// requests on it — one JSON message per text frame, each answered by its id — through the same
/// <see cref="McpDispatcher"/> as the HTTP listener, so consent, families and sealed secrets are the same. It needs no
/// Tailscale address and works through NAT. A dropped socket is dialled again with backoff (1 s doubling to 60 s); a
/// newer connection from this device replacing it (close 4000) is not fought over.
/// </summary>
public sealed class McpSocketHost : IAsyncDisposable
{
    public const int ReplacedCloseCode = 4000;
    private readonly McpSocketOptions _opt;
    private readonly McpDispatcher _dispatch;
    private readonly SemaphoreSlim _send = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ClientWebSocket? _ws;

    public McpSocketHost(McpSocketOptions options, McpDispatcher dispatcher)
    {
        _opt = options;
        _dispatch = dispatcher;
    }

    /// <summary>True between Start and Stop, whether or not the socket is up at this moment.</summary>
    public bool IsRunning => _loop is { IsCompleted: false };
    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public string? LastError { get; private set; }
    /// <summary>Raised when the socket opens or closes, from a background thread.</summary>
    public event Action? StateChanged;

    /// <summary>The address it dials, for a person to read (no token in it).</summary>
    public string Address => _opt.Endpoint.ToString();

    public static Uri EndpointFor(Uri hub)
    {
        var b = new UriBuilder(hub) { Scheme = hub.Scheme == Uri.UriSchemeHttp ? "ws" : "wss", Path = "/api/v1/mcp/host", Query = "" };
        if (hub.IsDefaultPort) b.Port = -1;
        return b.Uri;
    }

    public void Start()
    {
        if (IsRunning) return;
        LastError = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(ct));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;
        try
        {
            if (_ws is { State: WebSocketState.Open } ws)
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "DocaDesk stopped", bye.Token).ConfigureAwait(false);
            }
        }
        catch { /* already gone */ }
        cts.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch { /* stopped */ }
        _loop = null;
        cts.Dispose();
    }

    /// <summary>Tell the hub the tool list changed (a local server started, a family granted): it lists again.</summary>
    public void NotifyToolsChanged() =>
        _ = SendAsync("""{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""", CancellationToken.None);

    private async Task RunAsync(CancellationToken ct)
    {
        var wait = _opt.FirstRetry;
        while (!ct.IsCancellationRequested)
        {
            var closedBy = await DialOnceAsync(ct).ConfigureAwait(false);
            StateChanged?.Invoke();
            if (ct.IsCancellationRequested) break;
            if (closedBy == ReplacedCloseCode)
            {
                LastError = "Another connection from this device replaced this one (DocaDesk open twice?). Turn the listener off and on to take it back.";
                _opt.Audit?.Add("mcp.socket", LastError);
                StateChanged?.Invoke();
                break;
            }
            if (closedBy is not null) wait = _opt.FirstRetry;   // it was up: start the backoff again
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, _opt.MaxRetry.Ticks));
        }
    }

    /// <summary>One connection: null when it never opened, else the close code the hub sent (0 when none).</summary>
    private async Task<int?> DialOnceAsync(CancellationToken ct)
    {
        var token = _opt.Token();
        if (string.IsNullOrEmpty(token)) { LastError = "Not paired: there is no token to open the socket with."; return null; }
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
        ws.Options.KeepAliveInterval = _opt.Keepalive;
        if (_opt.TrustCertificate is { } trust)
            ws.Options.RemoteCertificateValidationCallback = (_, cert, chain, errors) => trust(cert, chain, errors);
        try
        {
            using var dial = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dial.CancelAfter(TimeSpan.FromSeconds(30));
            await ws.ConnectAsync(_opt.Endpoint, dial.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LastError = $"Cannot open the socket to {_opt.Endpoint.Host}: {Why(ex)}";
            return null;
        }
        catch { return null; }

        _ws = ws;
        LastError = null;
        _opt.Audit?.Add("mcp.socket", $"connected to {_opt.Endpoint.Host}");
        StateChanged?.Invoke();
        using var keep = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keepalive = KeepaliveAsync(keep.Token);
        try
        {
            var buffer = new byte[64 * 1024];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, r.Count);
                    if (message.Length > 32 * 1024 * 1024) throw new InvalidOperationException("a message over 32 MB");
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    // Finish the close handshake the hub began, so its side ends cleanly.
                    try
                    {
                        using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", bye.Token).ConfigureAwait(false);
                    }
                    catch { /* gone */ }
                    break;
                }
                if (r.MessageType != WebSocketMessageType.Text) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                _ = AnswerAsync(text, ct);   // a long tools/call must not hold up the next request
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LastError = $"The socket to {_opt.Endpoint.Host} dropped: {Why(ex)}";
        }
        catch { /* stopping */ }
        finally
        {
            keep.Cancel();
            try { await keepalive.ConfigureAwait(false); } catch { /* stopped */ }
            _ws = null;
        }
        var code = (int?)ws.CloseStatus ?? 0;
        _opt.Audit?.Add("mcp.socket", $"closed ({code}{(string.IsNullOrEmpty(ws.CloseStatusDescription) ? "" : " " + ws.CloseStatusDescription)})");
        return code;
    }

    private async Task AnswerAsync(string text, CancellationToken ct)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(text); } catch { return; }
        // A notification (no id) is answered by nothing — JSON-RPC's rule — and an answer to us we never asked for.
        if (msg is not JsonObject o || !o.ContainsKey("id") || !o.ContainsKey("method")) return;
        var reply = await _dispatch.DispatchAsync(text, "socket").ConfigureAwait(false);
        await SendAsync(reply, ct).ConfigureAwait(false);
    }

    private async Task KeepaliveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_opt.Keepalive, ct).ConfigureAwait(false);
            await SendAsync("""{"jsonrpc":"2.0","method":"notifications/keepalive"}""", ct).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(string json, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        catch { /* the read loop sees the socket go */ }
        finally { _send.Release(); }
    }

    private static string Why(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        var msg = ex.Message;
        if (msg.Contains("401")) return "the hub refused the token (401) — pair again.";
        if (msg.Contains("403")) return "this device's token lacks mcp:self (403).";
        return ReferenceEquals(inner, ex) ? msg : $"{msg} ({inner.Message})";
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
