using System.Net;
using System.Net.Sockets;
using System.Text;
using DocaDesk.Mcp;

namespace DocaDesk.Tests;

/// <summary>
/// DOCA (2.90.0+) holds the listener's GET event stream open when initialize says
/// tools.listChanged, and lists the tools again on notifications/tools/list_changed —
/// so a local server started later reaches the dashboard without ↺ Tools.
/// </summary>
public class McpListChangedTests
{
    [Fact]
    public async Task Initialize_announces_list_changed_and_the_event_stream_carries_it()
    {
        var port = FreePort();
        await using var listener = new McpHttpListener(new McpListenerOptions { PathSecret = McpHttpListener.NewPathSecret() });
        listener.StreamHeartbeat = TimeSpan.FromMilliseconds(300);
        await listener.StartLoopbackForTestsAsync(port);

        using var http = new HttpClient();
        var init = await http.PostAsync(listener.BoundUrl, new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", Encoding.UTF8, "application/json"));
        Assert.Contains("\"listChanged\":true", await init.Content.ReadAsStringAsync());

        using var req = new HttpRequestMessage(HttpMethod.Get, listener.BoundUrl);
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
        Assert.Equal(": open", await reader.ReadLineAsync());
        await reader.ReadLineAsync();   // the blank line ending the comment

        for (var i = 0; i < 50 && listener.OpenStreams == 0; i++) await Task.Delay(20);
        Assert.Equal(1, listener.OpenStreams);
        listener.NotifyToolsChanged();
        var seen = new StringBuilder();
        for (var i = 0; i < 10 && !seen.ToString().Contains("list_changed"); i++) seen.AppendLine(await reader.ReadLineAsync());
        Assert.Contains("\"method\":\"notifications/tools/list_changed\"", seen.ToString());

        res.Dispose();
        for (var i = 0; i < 100 && listener.OpenStreams > 0; i++) await Task.Delay(20);   // the heartbeat finds it gone
        Assert.Equal(0, listener.OpenStreams);
    }

    [Fact]
    public async Task The_event_stream_keeps_the_same_gates_as_a_post()
    {
        var port = FreePort();
        await using var listener = new McpHttpListener(new McpListenerOptions { PathSecret = McpHttpListener.NewPathSecret() });
        await listener.StartLoopbackForTestsAsync(port);
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/mcp/wrong-secret");
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var res = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(0, listener.OpenStreams);
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
