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

    /// <summary>The battery is a variable and the caps a PATCH of the device itself (hub audit 2026-10-06, cl 9–10).</summary>
    [Fact]
    public async Task Battery_goes_to_vars_and_caps_to_devices_me()
    {
        var prefix = $"http://127.0.0.1:{GetFreePort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        var seen = new List<string>();
        var serve = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var ctx = await listener.GetContextAsync();
                using var reader = new StreamReader(ctx.Request.InputStream);
                seen.Add($"{ctx.Request.HttpMethod} {ctx.Request.Url!.AbsolutePath} {await reader.ReadToEndAsync()}");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync("{}"u8.ToArray());
                ctx.Response.Close();
            }
        });
        await using var client = new DocaClient(new DocaClientOptions { BaseAddress = new Uri(prefix), Token = "t" });
        await client.PatchVarsAsync(new Dictionary<string, object?> { ["batteryPct"] = 58 });
        await client.PatchOwnCapsAsync(new DocaDesk.Core.Models.DeviceCaps());
        await serve;
        listener.Stop();
        Assert.StartsWith("PATCH /api/v1/devices/me/vars", seen[0]);
        Assert.Contains("\"batteryPct\":58", seen[0]);
        Assert.StartsWith("PATCH /api/v1/devices/me ", seen[1]);
        Assert.Contains("\"caps\"", seen[1]);
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
