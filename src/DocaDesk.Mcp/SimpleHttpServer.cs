using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace DocaDesk.Mcp;

/// <summary>
/// Minimal HTTP/1.1 POST server on a specific IP (no HttpListener URL ACL).
/// Enough for Doca's MCP client.
/// </summary>
public sealed class SimpleHttpServer : IAsyncDisposable
{
    private readonly IPAddress _bind;
    private readonly int _port;
    private readonly Func<HttpRequest, Task<HttpResponse>> _handler;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SimpleHttpServer(IPAddress bind, int port, Func<HttpRequest, Task<HttpResponse>> handler)
    {
        _bind = bind;
        _port = port;
        _handler = handler;
    }

    public bool IsRunning => _listener is not null;

    public void Start()
    {
        _listener = new TcpListener(_bind, _port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
            _loop = null;
        }
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                continue;
            }

            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(requestLine)) return;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0];
            var path = parts[1];

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(line)) break;
                var idx = line.IndexOf(':');
                if (idx > 0)
                    headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }

            string body = "";
            if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) && len > 0)
            {
                var buf = new char[len];
                var read = 0;
                while (read < len)
                {
                    var n = await reader.ReadAsync(buf.AsMemory(read, len - read), ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    read += n;
                }
                body = new string(buf, 0, read);
            }

            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            var req = new HttpRequest(method, path, headers, body, remote);
            var res = await _handler(req).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetBytes(res.Body);
            var header =
                $"HTTP/1.1 {res.StatusCode} {(res.StatusCode == 200 ? "OK" : "ERR")}\r\n" +
                $"Content-Type: {res.ContentType}\r\n" +
                $"Content-Length: {payload.Length}\r\n" +
                "Connection: close\r\n\r\n";
            var headerBytes = Encoding.UTF8.GetBytes(header);
            await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        catch
        {
            // drop
        }
        finally
        {
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

public sealed record HttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body, IPAddress? Remote);
public sealed record HttpResponse(int StatusCode, string ContentType, string Body);
