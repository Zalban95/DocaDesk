using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using DocaDesk.Mcp;

namespace DocaDesk.Tests;

/// <summary>
/// The socket transport (PROTOCOL.md §22.2): DocaDesk dials the hub and is an MCP server on that socket. A stub hub
/// here plays modules/mcp/socket-hosts.js — checks the bearer, sends JSON-RPC requests one per text frame, and finally
/// replaces the connection (close 4000), which the desk must not fight over.
/// </summary>
public class McpSocketHostTests
{
    [Theory]
    [InlineData("https://hub.example.ts.net:4242/", "wss://hub.example.ts.net:4242/api/v1/mcp/host")]
    [InlineData("https://hub.example/", "wss://hub.example/api/v1/mcp/host")]
    [InlineData("http://127.0.0.1:9000/", "ws://127.0.0.1:9000/api/v1/mcp/host")]
    public void The_endpoint_is_the_hubs_own_address(string hub, string ws) =>
        Assert.Equal(ws, McpSocketHost.EndpointFor(new Uri(hub)).ToString());

    [Fact]
    public async Task Answers_the_hubs_requests_on_the_socket_it_opened()
    {
        var port = FreePort();
        using var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();

        var echo = new EchoTool();
        var consent = new ToolConsent();
        consent.Register("echo", true);
        var dispatcher = new McpDispatcher(new McpListenerOptions { PathSecret = "unused", Tools = [echo], Consent = consent });
        await using var host = new McpSocketHost(new McpSocketOptions
        {
            Endpoint = new Uri($"ws://127.0.0.1:{port}/api/v1/mcp/host"),
            Token = () => "tok_desk",
            Keepalive = TimeSpan.FromMilliseconds(200),
        }, dispatcher);
        host.Start();

        var ctx = await WithTimeout(http.GetContextAsync());
        Assert.Equal("/api/v1/mcp/host", ctx.Request.Url!.AbsolutePath);
        Assert.Equal("Bearer tok_desk", ctx.Request.Headers["Authorization"]);
        var wsc = await ctx.AcceptWebSocketAsync(null);
        var ws = wsc.WebSocket;

        await Send(ws, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        var init = await ReceiveAnswer(ws);
        Assert.Equal(1, init["id"]!.GetValue<int>());
        Assert.Equal("DocaDesk", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());

        await Send(ws, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");   // a notification: no answer
        await Send(ws, """{"jsonrpc":"2.0","id":"two","method":"tools/list"}""");
        var list = await ReceiveAnswer(ws);
        Assert.Equal("two", list["id"]!.GetValue<string>());
        Assert.Contains("\"echo\"", list.ToJsonString());

        await Send(ws, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"say":"hi"}}}""");
        var call = await ReceiveAnswer(ws);
        Assert.Equal(3, call["id"]!.GetValue<int>());
        Assert.Equal("hi", call["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.True(host.IsConnected);

        // The keepalive arrives on its own (a notification, never answered by the hub).
        var keep = await WithTimeout(Receive(ws));
        Assert.Contains("notifications/", keep);

        try { await ws.CloseAsync((WebSocketCloseStatus)McpSocketHost.ReplacedCloseCode, "replaced by a newer connection", CancellationToken.None); }
        catch (WebSocketException) { /* the desk let go first */ }
        for (var i = 0; i < 100 && host.IsRunning; i++) await Task.Delay(20);
        Assert.False(host.IsRunning);
        Assert.Contains("replaced", host.LastError);
    }

    [Fact]
    public async Task A_hub_that_cannot_be_reached_is_said_and_dialled_again()
    {
        var port = FreePort();   // nothing listens
        await using var host = new McpSocketHost(new McpSocketOptions
        {
            Endpoint = new Uri($"ws://127.0.0.1:{port}/api/v1/mcp/host"),
            Token = () => "tok",
            FirstRetry = TimeSpan.FromMilliseconds(50),
        }, new McpDispatcher(new McpListenerOptions { PathSecret = "unused" }));
        host.Start();
        for (var i = 0; i < 100 && host.LastError is null; i++) await Task.Delay(20);
        Assert.Contains("Cannot open the socket", host.LastError);
        Assert.True(host.IsRunning);   // still trying
        await host.StopAsync();
        Assert.False(host.IsRunning);
    }

    private sealed class EchoTool : IMcpTool
    {
        public string Name => "echo";
        public string Description => "echo";
        public bool ReadOnlyHint => true;
        public JsonObject InputSchema => new() { ["type"] = "object" };
        public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct) =>
            Task.FromResult(new McpToolResult { Text = args?["say"]?.GetValue<string>() ?? "" });
    }

    private static Task Send(WebSocket ws, string json) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> Receive(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buffer, CancellationToken.None); ms.Write(buffer, 0, r.Count); } while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>The next answer (a message with an id), skipping keepalives.</summary>
    private static async Task<JsonNode> ReceiveAnswer(WebSocket ws)
    {
        while (true)
        {
            var node = JsonNode.Parse(await WithTimeout(Receive(ws)))!;
            if (node is JsonObject o && o.ContainsKey("id")) return node;
        }
    }

    private static async Task<T> WithTimeout<T>(Task<T> t)
    {
        if (await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(10))) != t) throw new TimeoutException();
        return await t;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
