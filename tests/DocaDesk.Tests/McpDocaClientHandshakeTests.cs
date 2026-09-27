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

    /// <summary>
    /// The `files` family through the real stack, the way DOCA's Files tab reaches it:
    /// `modules/mcp/client.js` calls the tool, and `modules/device-files.js` checks the name, looks
    /// for an `Error:` prefix, then `JSON.parse`s the text. Those are the three traps in
    /// ISSUES.md D-16, and each one fails *quietly* — a refusal parsed as a result, a name that
    /// never matches, a 502 on unparseable text — so asserting them against our own expectations
    /// would prove nothing. This runs the other side's code.
    /// </summary>
    [Fact]
    public async Task Doca_can_list_this_machines_files_and_is_refused_without_the_grant()
    {
        var clientJs = DocaRepo.ClientJs;
        if (clientJs is null) return;

        var dir = Path.Combine(Path.GetTempPath(), "docadesk-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "hello.txt"), "hi");

        var consent = FamilyConsent.InMemory();          // files not granted yet
        var toolConsent = new ToolConsent();
        foreach (var n in FilesTools.Names) toolConsent.Register(n, true);

        await using var listener = new McpHttpListener(new McpListenerOptions
        {
            PathSecret = McpHttpListener.NewPathSecret(),
            Tools = FilesTools.Create(consent, null),
            Consent = toolConsent,
        });
        await listener.StartLoopbackForTestsAsync(FreePort());
        var url = listener.BoundUrl!;

        // device-files.js does exactly this: name check, Error: prefix, JSON.parse.
        var script = $$"""
            const { McpClient } = require({{ToJsString(clientJs!)}});
            const call = async (c, tool, args) => {
              if (!c.tools.some(t => t.name === tool)) throw new Error('not offered: ' + tool);
              const text = await c.callTool(tool, args);
              if (/^Error:/.test(text)) return { refused: text.replace(/^Error:\s*/, '') };
              return { ok: JSON.parse(text) };
            };
            (async () => {
              const c = new McpClient({ id: 'desk-files', transport: 'http', url: {{ToJsString(url)}} });
              await c.start();

              const denied = await call(c, 'files_list', { path: {{ToJsString(dir)}} });
              if (!denied.refused) throw new Error('an ungranted family answered: ' + JSON.stringify(denied));

              process.stdout.write('REFUSED\n');
              c.stop();
            })().catch(e => { console.error(e); process.exit(1); });
            """;

        var (code, stdout, stderr) = await RunNodeAsync(script);
        Assert.True(code == 0, $"refusal leg failed: {stderr}{stdout}");
        Assert.Contains("REFUSED", stdout);

        // Now the person allows it, and the same call has to come back as parseable JSON.
        consent.SetGranted(ToolFamilies.Files, true);

        var script2 = $$"""
            const { McpClient } = require({{ToJsString(clientJs!)}});
            (async () => {
              const c = new McpClient({ id: 'desk-files2', transport: 'http', url: {{ToJsString(url)}} });
              await c.start();
              const text = await c.callTool('files_list', { path: {{ToJsString(dir)}} });
              if (/^Error:/.test(text)) throw new Error('refused after the grant: ' + text);
              const doc = JSON.parse(text);                       // a 502 in device-files.js if this throws
              const names = (doc.entries || []).map(e => e.name);
              if (!names.includes('hello.txt')) throw new Error('missing entry: ' + names.join(','));
              const f = doc.entries.find(e => e.name === 'hello.txt');
              if (f.isDir !== false || f.size !== 2 || !f.mtime) throw new Error('wrong shape: ' + JSON.stringify(f));

              const read = JSON.parse(await c.callTool('files_read', { path: {{ToJsString(dir)}} + '\\hello.txt' }));
              if (read.content !== 'hi') throw new Error('wrong content: ' + JSON.stringify(read));

              process.stdout.write('OK\n');
              c.stop();
            })().catch(e => { console.error(e); process.exit(1); });
            """;

        var (code2, stdout2, stderr2) = await RunNodeAsync(script2);
        Assert.True(code2 == 0, $"granted leg failed: {stderr2}{stdout2}");
        Assert.Contains("OK", stdout2);

        Directory.Delete(dir, recursive: true);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunNodeAsync(string script)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "docadesk-mcp-" + Guid.NewGuid().ToString("N") + ".js");
        await File.WriteAllTextAsync(tmp, script);
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { tmp },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("node failed to start");

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));   // D-14
            var outTask = p.StandardOutput.ReadToEndAsync(deadline.Token);
            var errTask = p.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await p.WaitForExitAsync(deadline.Token);
                return (p.ExitCode, await outTask, await errTask);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return (-1, "", "node did not exit within 60s (it was killed).");
            }
        }
        finally { File.Delete(tmp); }
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
