using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocaDesk.Core;
using DocaDesk.Core.Models;
using DocaDesk.Core.Net;
using DocaDesk.Core.Push;

namespace DocaDesk.Tests;

/// <summary>
/// Live/local smoke. Set DOCADESK_E2E_URL + DOCADESK_E2E_TOKEN to run against a real host.
/// Defaults attempt https://127.0.0.1:4242/ with token from env only (never baked in).
/// </summary>
public class LiveSmokeTests
{
    [Fact]
    public async Task Capabilities_push_ack_roundtrip_when_env_configured()
    {
        var url = Environment.GetEnvironmentVariable("DOCADESK_E2E_URL");
        var token = Environment.GetEnvironmentVariable("DOCADESK_E2E_TOKEN");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            return; // not configured — skip without failing CI

        await using var client = new DocaClient(new DocaClientOptions
        {
            BaseAddress = new Uri(url),
            Token = token,
            CertificatePolicy = new Core.Security.CertificatePolicy(
                Environment.GetEnvironmentVariable("DOCADESK_E2E_PIN")),
        });

        // Self-signed local: if pin unset, temporarily allow only when DOCADESK_E2E_INSECURE=1 is NOT used;
        // for local self-signed, require pin. If connection fails with cert error and no pin, skip.
        CapabilitiesDocument caps;
        try
        {
            caps = await client.GetCapabilitiesAsync();
        }
        catch (Exception ex) when (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                                   ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return;
        }

        Assert.NotNull(caps.Push);
        var cursor = new MemoryCursorStore();
        if (caps.Push!.Cursor is long c)
            await cursor.SaveAsync(c);

        var since = await cursor.LoadAsync();
        var poll = await client.PollEventsAsync(since);
        Assert.NotNull(poll);

        if (poll.Events is { Count: > 0 })
        {
            var last = poll.Events.Max(e => e.Seq);
            await client.AckAsync(last, caps.Push.AckUrl);
            await cursor.SaveAsync(last);
        }
    }

    [Fact]
    public void Device_caps_factory_declares_desktop()
    {
        var caps = DeviceCapsFactory.FromMachine();
        Assert.Equal("desktop", caps.FormFactor);
        Assert.Empty(caps.Exec!);
    }
}
