using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DocaDesk.Mcp;

/// <summary>
/// This machine as the relay that serves a page or a sound to a TV or speaker on its own network (DOCA PROTOCOL §22.5
/// <c>cast_serve</c>; the same rules as doca-client's <c>clients/node/cast.js</c> and DocaMobile's <c>CastDoor.kt</c>):
/// a plain-HTTP listener on this machine's address on the target's network, a random port, answering the target's
/// address alone. A page cast forwards each request to the hub through the app's pinned connection, adding no token or
/// cookie of its own (the hub sees the TV's own view session): Host and Origin become the hub's, Set-Cookie loses
/// Secure (the TV holds it over HTTP), answers stream. A sound comes with the cast and is served from here, nothing
/// forwarded. A door closes at <see cref="End"/>, or by itself after its time.
/// </summary>
public sealed partial class CastDoor : IDisposable
{
    public const int MaxMedia = 10 * 1024 * 1024;
    private static readonly HashSet<string> Hop = new(StringComparer.OrdinalIgnoreCase)
        { "connection", "keep-alive", "proxy-connection", "transfer-encoding", "upgrade", "te", "trailer", "authorization", "host", "content-length" };

    [GeneratedRegex("^/api/cast/media/([0-9a-f]{32})$")] private static partial Regex Token();
    [GeneratedRegex(@";\s*secure", RegexOptions.IgnoreCase)] private static partial Regex Secure();

    public sealed record Media(byte[] Bytes, string Mime);

    private sealed class Door(TcpListener server, string target, bool page, string @base)
    {
        public TcpListener Server { get; } = server;
        public string Target { get; } = target;
        public volatile bool Page = page;
        public ConcurrentDictionary<string, Media> Files { get; } = new();
        public string Base { get; } = @base;
        public long Until;
    }

    private readonly Func<HttpMessageInvoker?> _hub;
    private readonly Func<Uri?> _hubBase;
    private readonly ConcurrentDictionary<string, Door> _doors = new();

    /// <param name="hub">A connection to the hub that adds nothing of this machine's: no token, no cookies, no redirects.</param>
    /// <param name="hubBase">The hub's address.</param>
    public CastDoor(Func<HttpMessageInvoker?> hub, Func<Uri?> hubBase)
    {
        _hub = hub;
        _hubBase = hubBase;
    }

    private static long Now => Environment.TickCount64;

    /// <summary>Open (or reuse) the door for <paramref name="cast"/> on <paramref name="address"/> — this machine's address on the target's network. → its base.</summary>
    public string Serve(string cast, string target, IPAddress address, bool page, IReadOnlyDictionary<string, Media> media, TimeSpan ttl)
    {
        if (media.Values.Sum(m => (long)m.Bytes.Length) > MaxMedia) throw new ArgumentException("At most 10 MB of media in one cast.");
        if (_doors.TryGetValue(cast, out var was) && was.Target == target)
        {
            foreach (var (k, v) in media) was.Files[k] = v;
            was.Page = was.Page || page;
            was.Until = Now + (long)ttl.TotalMilliseconds;
            return was.Base;
        }
        if (was is not null) End(cast);
        var server = new TcpListener(address, 0);
        server.Start(16);
        var door = new Door(server, target, page, $"http://{address}:{((IPEndPoint)server.LocalEndpoint).Port}") { Until = Now + (long)ttl.TotalMilliseconds };
        foreach (var (k, v) in media) door.Files[k] = v;
        _doors[cast] = door;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient s;
                try { s = await server.AcceptTcpClientAsync().ConfigureAwait(false); } catch { break; }
                if (Now > door.Until) { s.Dispose(); End(cast); break; }
                _ = Task.Run(async () => { using (s) { try { await HandleAsync(door, s).ConfigureAwait(false); } catch { /* the TV went away */ } } });
            }
        });
        return door.Base;
    }

    public bool End(string cast)
    {
        if (!_doors.TryRemove(cast, out var d)) return false;
        try { d.Server.Stop(); } catch { /* closed */ }
        return true;
    }

    public void Dispose()
    {
        foreach (var k in _doors.Keys.ToList()) End(k);
    }

    private async Task HandleAsync(Door door, TcpClient s)
    {
        var net = s.GetStream();
        var from = ((IPEndPoint)s.Client.RemoteEndPoint!).Address;
        if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
        if (from.ToString() != door.Target) { await PlainAsync(net, 404, "Not here.").ConfigureAwait(false); return; }

        var line = await ReadLineAsync(net).ConfigureAwait(false);
        if (line is null) return;
        var parts = line.Split(' ');
        if (parts.Length < 2) { await PlainAsync(net, 400, "Bad request.").ConfigureAwait(false); return; }
        var (method, path) = (parts[0], parts[1]);
        var headers = new List<(string, string)>();
        var length = 0;
        while (await ReadLineAsync(net).ConfigureAwait(false) is { Length: > 0 } h)
        {
            var i = h.IndexOf(':');
            if (i <= 0) continue;
            var (k, v) = (h[..i].Trim(), h[(i + 1)..].Trim());
            if (k.Equals("content-length", StringComparison.OrdinalIgnoreCase)) _ = int.TryParse(v, out length);
            headers.Add((k, v));
        }
        byte[]? body = null;
        if (length is > 0 and <= 5 << 20)
        {
            body = new byte[length];
            await net.ReadExactlyAsync(body).ConfigureAwait(false);
        }
        if (!door.Page) door.Until = Math.Max(door.Until, Now + 10 * 60_000);

        var m = Token().Match(path.Split('?')[0]);
        if (m.Success)
        {
            if (!door.Files.TryGetValue(m.Groups[1].Value, out var f) || (method != "GET" && method != "HEAD"))
            {
                await PlainAsync(net, 404, "Not here.").ConfigureAwait(false);
                return;
            }
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: {f.Mime}\r\nContent-Length: {f.Bytes.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
            await net.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
            if (method == "GET") await net.WriteAsync(f.Bytes).ConfigureAwait(false);
            return;
        }
        if (!door.Page) { await PlainAsync(net, 404, "Not here.").ConfigureAwait(false); return; }
        await ForwardAsync(door, method, path, headers, body, net).ConfigureAwait(false);
    }

    /// <summary>One request to the hub, adding nothing of ours; the answer streamed back.</summary>
    private async Task ForwardAsync(Door door, string method, string path, List<(string Key, string Value)> headers, byte[]? body, Stream net)
    {
        var hub = _hub();
        var hubBase = _hubBase();
        if (hub is null || hubBase is null) { await PlainAsync(net, 502, "The hub did not answer.").ConfigureAwait(false); return; }
        var origin = hubBase.GetLeftPart(UriPartial.Authority);
        using var req = new HttpRequestMessage(new HttpMethod(method), origin + path);
        if (method is not ("GET" or "HEAD")) req.Content = new ByteArrayContent(body ?? []);
        foreach (var (k, v) in headers)
        {
            if (Hop.Contains(k) || k.StartsWith("x-doca-", StringComparison.OrdinalIgnoreCase)) continue;
            var value = k.ToLowerInvariant() switch { "origin" => origin, "referer" => v.Replace(door.Base, origin), _ => v };
            if (!req.Headers.TryAddWithoutValidation(k, value)) req.Content?.Headers.TryAddWithoutValidation(k, value);
        }
        HttpResponseMessage res;
        try { res = await hub.SendAsync(req, CancellationToken.None).ConfigureAwait(false); }
        catch { await PlainAsync(net, 502, "The hub did not answer.").ConfigureAwait(false); return; }
        using (res)
        {
            var sb = new StringBuilder($"HTTP/1.1 {(int)res.StatusCode} {(string.IsNullOrEmpty(res.ReasonPhrase) ? "OK" : res.ReasonPhrase)}\r\n");
            foreach (var (k, values) in res.Headers.Concat(res.Content.Headers))
            {
                if (Hop.Contains(k) || k.Equals("content-encoding", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var v in values)
                {
                    var value = k.ToLowerInvariant() switch { "set-cookie" => Secure().Replace(v, ""), "location" => v.Replace(origin, door.Base), _ => v };
                    sb.Append(k).Append(": ").Append(value).Append("\r\n");
                }
            }
            sb.Append("Connection: close\r\n\r\n");
            await net.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString())).ConfigureAwait(false);
            await net.FlushAsync().ConfigureAwait(false);
            await using var src = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buf = new byte[8192];
            int n;
            while ((n = await src.ReadAsync(buf).ConfigureAwait(false)) > 0)
            {
                await net.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                await net.FlushAsync().ConfigureAwait(false);   // a page's live feed is SSE
            }
        }
    }

    private static async Task PlainAsync(Stream net, int code, string text)
    {
        var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 {code} {(code == 404 ? "Not Found" : "Error")}\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(text)}\r\nConnection: close\r\n\r\n{text}");
        await net.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineAsync(Stream input)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one).ConfigureAwait(false) <= 0) return sb.Length == 0 ? null : sb.ToString();
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            if (sb.Length > 8192) return null;
            sb.Append((char)one[0]);
        }
    }
}
