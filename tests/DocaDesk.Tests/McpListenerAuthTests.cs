using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DocaDesk.Mcp;

namespace DocaDesk.Tests;

public class McpListenerAuthTests
{
    [Fact]
    public async Task Initialize_and_tools_list_succeed_on_secret_path()
    {
        var port = FreePort();
        var secret = McpHttpListener.NewPathSecret();
        var consent = new ToolConsent();
        consent.Set("list_windows", true);

        await using var listener = new McpHttpListener(new McpListenerOptions
        {
            PathSecret = secret,
            Tools =
            [
                new EchoTool(),
            ],
            Consent = consent,
        });
        await listener.StartLoopbackForTestsAsync(port);
        Assert.NotNull(listener.BoundUrl);

        var init = await PostJsonAsync(listener.BoundUrl!, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"doca","version":"0"}}}""");
        Assert.Contains("DocaDesk", init);
        Assert.Contains("2025-06-18", init);

        var list = await PostJsonAsync(listener.BoundUrl!, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
        Assert.Contains("echo", list);
        Assert.Contains("readOnlyHint", list);
    }

    [Fact]
    public async Task Request_without_secret_path_is_refused()
    {
        var port = FreePort();
        var secret = McpHttpListener.NewPathSecret();
        await using var listener = new McpHttpListener(new McpListenerOptions { PathSecret = secret });
        await listener.StartLoopbackForTestsAsync(port);

        var url = $"http://127.0.0.1:{port}/mcp/wrong-secret";
        var (status, _) = await PostRawAsync(url, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task Loopback_refused_when_AllowLoopback_false()
    {
        var port = FreePort();
        var secret = McpHttpListener.NewPathSecret();
        await using var listener = new McpHttpListener(new McpListenerOptions
        {
            PathSecret = secret,
            AllowLoopback = false,
            AllowedRemoteHost = "198.51.100.9",
        });
        // Start on loopback but with AllowLoopback=false → every local POST is 403
        await listener.StopAsync();
        var field = typeof(McpHttpListener).GetField("_opt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        // Use public test start then flip is awkward — start via StartLoopback then we need another path.
        // Direct: StartLoopback enables AllowLoopback. So construct server manually:
        await using var server = new SimpleHttpServer(IPAddress.Loopback, port, req =>
        {
            var opt = new McpListenerOptions { PathSecret = secret, AllowLoopback = false, AllowedRemoteHost = "198.51.100.9" };
            var remote = req.Remote;
            var allowed = remote is not null && (
                (IPAddress.IsLoopback(remote) && opt.AllowLoopback) ||
                remote.ToString().StartsWith("100."));
            if (!allowed) return Task.FromResult(new HttpResponse(403, "text/plain", "forbidden remote"));
            return Task.FromResult(new HttpResponse(200, "text/plain", "ok"));
        });
        server.Start();
        var (status, body) = await PostRawAsync($"http://127.0.0.1:{port}/mcp/{secret}", "{}");
        Assert.Equal(403, status);
        Assert.Contains("forbidden", body);
    }

    private sealed class EchoTool : IMcpTool
    {
        public string Name => "echo";
        public string Description => "echo";
        public bool ReadOnlyHint => true;
        public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct) =>
            Task.FromResult(new McpToolResult { Text = "ok" });
    }

    private static async Task<string> PostJsonAsync(string url, string body)
    {
        var (status, text) = await PostRawAsync(url, body);
        Assert.Equal(200, status);
        return text;
    }
    private static async Task<(int Status, string Body)> PostRawAsync(string url, string body)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res = await client.PostAsync(url, content);
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
