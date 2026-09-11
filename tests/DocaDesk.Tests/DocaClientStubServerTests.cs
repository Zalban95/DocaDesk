using System.Net;
using System.Text;
using DocaDesk.Core.Net;

namespace DocaDesk.Tests;

public class DocaClientStubServerTests
{
    [Fact]
    public async Task GetCapabilities_against_HttpListener()
    {
        var prefix = $"http://127.0.0.1:{GetFreePort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            Assert.Equal("/api/v1/capabilities", ctx.Request.Url!.AbsolutePath);
            Assert.Equal("Bearer test-token", ctx.Request.Headers["Authorization"]);
            Assert.Contains("DocaDesk/", ctx.Request.Headers["X-Doca-Client"]);
            var payload = """{"push":{"heartbeatSec":25},"limits":{"mediaBytes":1572864},"extra":true}"""u8.ToArray();
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(payload);
            ctx.Response.Close();
        });

        await using var client = new DocaClient(new DocaClientOptions
        {
            BaseAddress = new Uri(prefix),
            Token = "test-token",
        });
        var caps = await client.GetCapabilitiesAsync();
        Assert.Equal(25, caps.Push!.HeartbeatSec);
        Assert.NotNull(caps.ExtensionData);

        await serve;
        listener.Stop();
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
