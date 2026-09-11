using System.Net;
using System.Text;
using DocaDesk.Core.Models;
using DocaDesk.Core.Net;
using DocaDesk.Core.Push;

namespace DocaDesk.Tests;

public class ResyncHandlingTests
{
    [Fact]
    public async Task Poll_resync_true_invokes_OnResync()
    {
        var prefix = $"http://127.0.0.1:{FreePort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var resyncHit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = Task.Run(async () =>
        {
            // First connection: SSE fails immediately → engine falls to poll
            // Actually drive poll path: make SSE return 503 so engine falls back
            var ctx = await listener.GetContextAsync();
            if (ctx.Request.Headers["Accept"]?.Contains("text/event-stream") == true)
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.Close();
                ctx = await listener.GetContextAsync();
            }

            var payload = """{"events":[],"nextSince":3,"resync":true}"""u8.ToArray();
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(payload);
            ctx.Response.Close();
        });

        await using var client = new DocaClient(new DocaClientOptions { BaseAddress = new Uri(prefix), Token = "t" });
        await using var engine = new PushEngine(new PushEngineOptions
        {
            Client = client,
            CursorStore = new MemoryCursorStore(),
            OnResync = (_, _) =>
            {
                resyncHit.TrySetResult();
                return Task.CompletedTask;
            },
        });
        engine.ConfigureFromCapabilities(new CapabilitiesDocument
        {
            Push = new PushCapabilities { HeartbeatSec = 25, Backoff = new BackoffCapabilities { InitialMs = 50, MaxMs = 100, Factor = 2, Jitter = 0 } },
        });
        engine.Start();

        await resyncHit.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync();
        listener.Stop();
        try { await server; } catch { /* ok */ }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
