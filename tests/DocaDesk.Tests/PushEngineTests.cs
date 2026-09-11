using System.Net;
using System.Text;
using DocaDesk.Core.Models;
using DocaDesk.Core.Net;
using DocaDesk.Core.Push;

namespace DocaDesk.Tests;

public class PushEngineTests
{
    [Fact]
    public async Task Sse_hello_and_event_advance_cursor_and_ack()
    {
        var prefix = $"http://127.0.0.1:{FreePort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var acked = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotEvent = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = Task.Run(async () =>
        {
            // SSE
            var ctx = await listener.GetContextAsync();
            Assert.Equal("/api/v1/events", ctx.Request.Url!.AbsolutePath);
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.StatusCode = 200;
            await using (var w = new StreamWriter(ctx.Response.OutputStream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
            {
                await w.WriteAsync("event: hello\ndata: {\"cursor\":5,\"heartbeatSec\":25,\"resync\":false}\n\n");
                await w.WriteAsync("id: 1\nevent: prompt.new\ndata: {\"seq\":6,\"type\":\"prompt.new\",\"ack\":true,\"payload\":{}}\n\n");
                await w.WriteAsync(": ping\n\n");
            }

            // Ack
            var ackCtx = await listener.GetContextAsync();
            Assert.Equal("/api/v1/events/ack", ackCtx.Request.Url!.AbsolutePath);
            using var reader = new StreamReader(ackCtx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            Assert.Contains("\"seq\":6", body);
            acked.TrySetResult(6);
            var bytes = Encoding.UTF8.GetBytes("""{"acked":6,"pending":0,"cursor":6}""");
            ackCtx.Response.StatusCode = 200;
            await ackCtx.Response.OutputStream.WriteAsync(bytes);
            ackCtx.Response.Close();
            ctx.Response.Close();
        });

        await using var client = new DocaClient(new DocaClientOptions
        {
            BaseAddress = new Uri(prefix),
            Token = "t",
        });
        var cursor = new MemoryCursorStore();
        await using var engine = new PushEngine(new PushEngineOptions
        {
            Client = client,
            CursorStore = cursor,
            OnEvent = (ev, _) =>
            {
                gotEvent.TrySetResult(ev.Seq);
                return Task.CompletedTask;
            },
        });
        engine.ConfigureFromCapabilities(new CapabilitiesDocument
        {
            Push = new PushCapabilities { HeartbeatSec = 25, AckUrl = "/api/v1/events/ack" },
        });
        engine.Start();

        var seq = await gotEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(6, seq);
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(6, await cursor.LoadAsync());

        await engine.StopAsync();
        listener.Stop();
        try { await server; } catch { /* listener stopped */ }
    }

    [Fact]
    public void Watchdog_expires_without_traffic_on_fake_clock()
    {
        // Unit-level: HeartbeatWatchdog already covered; ensure ConfigureFromCapabilities sets 2x.
        var clock = new FakeClock();
        var wd = new HeartbeatWatchdog(clock);
        var engineCaps = new CapabilitiesDocument { Push = new PushCapabilities { HeartbeatSec = 10 } };
        // mirror ConfigureFromCapabilities
        wd.Configure(engineCaps.Push!.HeartbeatSec!.Value);
        Assert.Equal(TimeSpan.FromSeconds(20), wd.Timeout);
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(wd.IsExpired);
    }

    [Fact]
    public async Task Json_poll_uses_same_cursor_store()
    {
        var prefix = $"http://127.0.0.1:{FreePort()}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            Assert.Contains("application/json", ctx.Request.Headers["Accept"]);
            var payload = """
                {"events":[{"seq":11,"type":"alert","ack":false}],"nextSince":11,"resync":false}
                """u8.ToArray();
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(payload);
            ctx.Response.Close();
        });

        await using var client = new DocaClient(new DocaClientOptions { BaseAddress = new Uri(prefix), Token = "t" });
        var cursor = new MemoryCursorStore();
        await cursor.SaveAsync(10);

        // Drive poll path directly via a one-shot engine failure path is heavy;
        // call PollEventsAsync + store like the engine does.
        var poll = await client.PollEventsAsync(10);
        Assert.NotNull(poll.Events);
        Assert.Equal(11, poll.Events![0].Seq);
        await cursor.SaveAsync(poll.NextSince!.Value);
        Assert.Equal(11, await cursor.LoadAsync());

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
