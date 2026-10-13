using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace DocaDesk.Mcp;

/// <summary>
/// A Cast v2 sender — the hub's <c>modules/cast/castv2/sender.js</c>, ported as DocaMobile's <c>CastSender.kt</c> is
/// (PROTOCOL §22.5 <c>cast_send</c>): one TLS connection to a Cast device's port 8009 (its certificate is the device's
/// own: no sender checks it, and nothing secret travels on it), the virtual connection to the receiver, PONG for every
/// PING and a PING every 5 s, requests matched to answers by requestId.
/// </summary>
public sealed class CastSender : IAsyncDisposable
{
    public const string Media = "CC1AD845";      // the Default Media Receiver
    public const string Dashcast = "84912283";   // a receiver that loads a URL
    private const string Sender = "sender-doca";

    public sealed record App(string AppId, string SessionId, string Transport);

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<CastWire.Message>> _waiting = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private int _rid;
    private volatile bool _closed;

    private CastSender(TcpClient tcp, Stream stream, TimeSpan timeout)
    {
        _tcp = tcp;
        _stream = stream;
        _timeout = timeout;
    }

    /// <summary>Connect to a Cast device; <paramref name="plain"/> opens an unencrypted socket (tests, a stand-in device).</summary>
    public static async Task<CastSender> ConnectAsync(string host, int port = 8009, bool plain = false, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(8));
            try { await tcp.ConnectAsync(host, port, connect.Token).ConfigureAwait(false); }
            catch (Exception) { tcp.Dispose(); throw new InvalidOperationException($"The Cast device at {host}:{port} did not answer."); }
        }
        Stream stream = tcp.GetStream();
        if (!plain)
        {
            // The device's own certificate, made by the device: there is nobody to check it against.
            var ssl = new SslStream(stream, false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct).ConfigureAwait(false);
            stream = ssl;
        }
        var s = new CastSender(tcp, stream, TimeSpan.FromSeconds(8));
        s.Start();
        return s;
    }

    public async Task SendAsync(string destination, string ns, JsonObject payload)
    {
        if (_closed) return;
        var bytes = CastWire.Encode(Sender, destination, ns, payload);
        await _write.WaitAsync().ConfigureAwait(false);
        try { await _stream.WriteAsync(bytes).ConfigureAwait(false); await _stream.FlushAsync().ConfigureAwait(false); }
        catch { /* the read loop ends the connection */ }
        finally { _write.Release(); }
    }

    private void Start()
    {
        var reader = new CastWire.Reader(m =>
        {
            var type = m.Data?["type"]?.GetValue<string>();
            if (m.Namespace == CastWire.NsHeartbeat && type == "PING")
            {
                _ = SendAsync(m.Source, CastWire.NsHeartbeat, new JsonObject { ["type"] = "PONG" });
                return;
            }
            if (m.Data?["requestId"] is JsonValue v && v.TryGetValue<int>(out var id) && _waiting.TryRemove(id, out var tcs))
                tcs.TrySetResult(m);
        });
        _ = Task.Run(async () =>
        {
            var buf = new byte[16384];
            try
            {
                while (!_closed)
                {
                    var n = await _stream.ReadAsync(buf, _life.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    reader.Feed(buf.AsSpan(0, n));
                }
            }
            catch { /* closed */ }
            await DisposeAsync().ConfigureAwait(false);
        });
        _ = SendAsync("receiver-0", CastWire.NsConnection, new JsonObject { ["type"] = "CONNECT" });
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_closed)
                {
                    await Task.Delay(5000, _life.Token).ConfigureAwait(false);
                    await SendAsync("receiver-0", CastWire.NsHeartbeat, new JsonObject { ["type"] = "PING" }).ConfigureAwait(false);
                }
            }
            catch { /* closed */ }
        });
    }

    /// <summary>Send and wait for the answer with the same requestId.</summary>
    public async Task<CastWire.Message> RequestAsync(string destination, string ns, JsonObject payload, TimeSpan? wait = null)
    {
        if (_closed) throw new InvalidOperationException("The Cast connection is closed.");
        var id = Interlocked.Increment(ref _rid);
        var tcs = new TaskCompletionSource<CastWire.Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = tcs;
        payload["requestId"] = id;
        var what = payload["type"]?.GetValue<string>();
        await SendAsync(destination, ns, payload).ConfigureAwait(false);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(wait ?? _timeout)).ConfigureAwait(false);
        if (done != tcs.Task) { _waiting.TryRemove(id, out _); throw new InvalidOperationException($"The Cast device did not answer {what}."); }
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<JsonObject> StatusAsync() =>
        (await RequestAsync("receiver-0", CastWire.NsReceiver, new JsonObject { ["type"] = "GET_STATUS" }).ConfigureAwait(false)).Data?["status"] as JsonObject ?? [];

    /// <summary>Launch (or find) an app and connect to its transport.</summary>
    public async Task<App> LaunchAsync(string appId)
    {
        var r = await RequestAsync("receiver-0", CastWire.NsReceiver, new JsonObject { ["type"] = "LAUNCH", ["appId"] = appId }, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var type = r.Data?["type"]?.GetValue<string>();
        if (type != "RECEIVER_STATUS")
            throw new InvalidOperationException($"The Cast device would not start the app: {r.Data?["reason"]?.GetValue<string>() ?? type ?? "no reason given"}.");
        var app = (r.Data?["status"]?["applications"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(a => a["appId"]?.GetValue<string>() == appId) ?? throw new InvalidOperationException("The Cast device started something else.");
        var transport = app["transportId"]?.GetValue<string>() ?? throw new InvalidOperationException("The Cast device named no transport.");
        await SendAsync(transport, CastWire.NsConnection, new JsonObject { ["type"] = "CONNECT" }).ConfigureAwait(false);
        return new App(appId, app["sessionId"]?.GetValue<string>() ?? "", transport);
    }

    public Task<CastWire.Message> StopAsync(string? sessionId)
    {
        var p = new JsonObject { ["type"] = "STOP" };
        if (sessionId is not null) p["sessionId"] = sessionId;
        return RequestAsync("receiver-0", CastWire.NsReceiver, p);
    }

    public Task<CastWire.Message> VolumeAsync(double level) =>
        RequestAsync("receiver-0", CastWire.NsReceiver, new JsonObject { ["type"] = "SET_VOLUME", ["volume"] = new JsonObject { ["level"] = Math.Clamp(level, 0, 1) } });

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        foreach (var t in _waiting.Values) t.TrySetException(new InvalidOperationException("The Cast device closed the connection."));
        _waiting.Clear();
        // Ended, not dropped: a message just written (DashCast's) still goes out before the socket closes.
        try { _tcp.Client.Shutdown(SocketShutdown.Send); } catch { /* already gone */ }
        _ = Task.Delay(2000).ContinueWith(_ => { _life.Cancel(); _stream.Dispose(); _tcp.Dispose(); }, TaskScheduler.Default);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
