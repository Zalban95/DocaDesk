using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DocaDesk.Mcp;

namespace DocaDesk.Tests;

public class McpDocaClientHandshakeTests
{
    [Fact]
    public async Task Doca_mcp_client_js_initialize_and_tools_list()
    {
        // Still a bare return (xunit 2.9.2 has no dynamic skip — TODO.md), so this reads as
        // passed where DOCA is absent. What changed is that it no longer reads as passed on a
        // machine that HAS DOCA: the path is resolved, not a literal that rotted (D-11).
        var clientJs = DocaRepo.ClientJs;
        if (clientJs is null) return;

        var port = FreePort();
        var secret = McpHttpListener.NewPathSecret();
        await using var listener = new McpHttpListener(new McpListenerOptions
        {
            PathSecret = secret,
            Tools = [new HelloTool()],
            Consent = new ToolConsent(),
        });
        await listener.StartLoopbackForTestsAsync(port);
        var url = listener.BoundUrl!;

        var script = $$"""
            const { McpClient } = require({{ToJsString(clientJs!)}});
            (async () => {
              const c = new McpClient({ id: 'desk-test', transport: 'http', url: {{ToJsString(url)}} });
              await c.start();
              const names = (c.tools || []).map(t => t.name).join(',');
              if (!names.includes('hello')) throw new Error('missing hello tool: ' + names);
              console.log('OK ' + names);
              c.stop();          // releases D-8's held-open event stream; see D-14
            })().catch(e => { console.error(e); process.exit(1); });
            """;

        var tmp = Path.Combine(Path.GetTempPath(), "docadesk-mcp-" + Guid.NewGuid().ToString("N") + ".js");
        await File.WriteAllTextAsync(tmp, script);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { tmp },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("node failed to start");
            // Bounded: since D-8 a client that holds the event stream open keeps node alive, and an
            // unbounded wait here is a hung suite rather than a failed test (D-14).
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var outTask = p.StandardOutput.ReadToEndAsync(deadline.Token);
            var errTask = p.StandardError.ReadToEndAsync(deadline.Token);
            string stdout, stderr;
            try
            {
                await p.WaitForExitAsync(deadline.Token);
                stdout = await outTask;
                stderr = await errTask;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new Xunit.Sdk.XunitException("node did not exit within 60s (it was killed).");
            }
            Assert.True(p.ExitCode == 0, $"node handshake failed: {stderr}\n{stdout}");
            Assert.Contains("OK", stdout);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private sealed class HelloTool : IMcpTool
    {
        public string Name => "hello";
        public string Description => "hi";
        public bool ReadOnlyHint => true;
        public System.Text.Json.Nodes.JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new System.Text.Json.Nodes.JsonObject() };
        public Task<McpToolResult> CallAsync(System.Text.Json.Nodes.JsonNode? args, string sessionId, CancellationToken ct) =>
            Task.FromResult(new McpToolResult { Text = "hi" });
    }

    private static string ToJsString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
