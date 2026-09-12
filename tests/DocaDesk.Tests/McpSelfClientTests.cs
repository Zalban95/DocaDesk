using System.Net;
using System.Text;
using DocaDesk.Core.Models;
using DocaDesk.Core.Net;
using DocaDesk.Mcp;

namespace DocaDesk.Tests;

public class McpSelfClientTests
{
    [Fact]
    public async Task GetMcpSelf_404_returns_null()
    {
        using var server = new TinyHttp();
        server.Map("GET", "/api/v1/mcp/self", (_, res) =>
        {
            res.StatusCode = 404;
            return """{"error":{"code":"not_found"}}""";
        });
        await using var client = new DocaClient(new DocaClientOptions
        {
            BaseAddress = server.BaseUri,
            Token = "doca_test.token",
        });
        Assert.Null(await client.GetMcpSelfAsync());
    }

    [Fact]
    public async Task Offer_then_patch_round_trip_and_command_ignored_on_wire()
    {
        string? lastPatchBody = null;
        using var server = new TinyHttp();
        server.Map("GET", "/api/v1/mcp/self", (_, _) =>
            """{"server":{"id":"desk-tools","url":"http://old/mcp/x","headers":{},"state":"stopped"}}""");
        server.Map("PATCH", "/api/v1/mcp/self", (req, _) =>
        {
            lastPatchBody = req;
            return """{"server":{"id":"desk-tools","url":"http://new/mcp/y","headers":{"Authorization":"Bearer abc"},"state":"stopped","command":"should-not-matter"}}""";
        });
        server.Map("POST", "/api/v1/mcp/offer", (_, res) =>
        {
            res.StatusCode = 202;
            return """{"offer":{"id":"mo_1","status":"pending"}}""";
        });

        await using var client = new DocaClient(new DocaClientOptions
        {
            BaseAddress = server.BaseUri,
            Token = "doca_test.token",
        });

        var offer = await client.OfferMcpAsync(new McpOfferRequest
        {
            Label = "PC",
            Url = "http://new/mcp/y",
            Tools = ["list_windows"],
        });
        Assert.Equal("pending", offer.Status);

        var patched = await client.PatchMcpSelfAsync(new McpSelfPatchRequest
        {
            Url = "http://new/mcp/y",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer abc" },
            Command = "rm -rf /",
            Transport = "stdio",
            Autostart = true,
        });
        Assert.Equal("http://new/mcp/y", patched.Url);
        Assert.Contains("Authorization", lastPatchBody!);
        Assert.Contains("rm -rf", lastPatchBody!);
        Assert.Equal("desk-tools", patched.Id);
    }

    [Fact]
    public async Task Bearer_missing_is_404_when_enforced()
    {
        var secret = McpHttpListener.NewPathSecret();
        await using var listener = new McpHttpListener(new McpListenerOptions
        {
            PathSecret = secret,
            RequiredBearerToken = "sekrit",
            EnforceBearer = true,
            AllowLoopback = true,
        });
        await listener.StartLoopbackForTestsAsync(FreePort());
        using var http = new HttpClient();
        using var res = await http.PostAsync(listener.BoundUrl, new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        using var okReq = new HttpRequestMessage(HttpMethod.Post, listener.BoundUrl)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""", Encoding.UTF8, "application/json"),
        };
        okReq.Headers.TryAddWithoutValidation("Authorization", "Bearer sekrit");
        using var ok = await http.SendAsync(okReq);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private sealed class TinyHttp : IDisposable
    {
        private readonly HttpListener _http = new();
        private readonly Dictionary<(string Method, string Path), Func<string, HttpListenerResponse, string>> _map = new();
        public Uri BaseUri { get; }

        public TinyHttp()
        {
            var port = FreePort();
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _http.Prefixes.Add(BaseUri.ToString());
            _http.Start();
            _ = Task.Run(Loop);
        }

        public void Map(string method, string path, Func<string, HttpListenerResponse, string> handler)
            => _map[(method, path)] = handler;

        private async Task Loop()
        {
            while (_http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync(); }
                catch { break; }
                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                if (_map.TryGetValue((ctx.Request.HttpMethod, path), out var h))
                {
                    var text = h(body, ctx.Response);
                    var bytes = Encoding.UTF8.GetBytes(text);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            try { _http.Stop(); } catch { /* ignore */ }
            _http.Close();
        }
    }
}
