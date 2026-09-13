using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocaDesk.Mcp;

namespace DocaDesk.Tests;

/// <summary>
/// The local MCP servers DocaDesk runs, end to end: a real child process
/// speaking newline-delimited JSON-RPC, its tools aggregated onto the listener
/// Doca talks to, and the consent gate in front of them.
///
/// The stub server is written out by the test itself, so nothing needs to be
/// installed but node — and without node the tests skip rather than fail.
/// </summary>
public class LocalMcpRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "docadesk-reg-" + Guid.NewGuid().ToString("N"));

    public LocalMcpRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a child may still hold it */ }
    }

    /* ── Lifecycle and tools ───────────────────────────── */

    [Fact]
    public async Task Running_server_contributes_namespaced_tools_only_once_consented()
    {
        if (!NodeAvailable()) return;                          // xunit 2.9.2 has no dynamic skip; see TODO.md
        await using var registry = NewRegistry(out var consent);
        registry.Add(StubSpec("stub"));

        Assert.True(await registry.StartAsync("stub"), $"stub did not start: {string.Join('\n', registry.LogOf("stub"))}");
        Assert.Equal(McpServerState.Running, registry.StateOf("stub"));

        // Consent is off by default, so the server runs but offers nothing.
        Assert.Empty(registry.Tools());

        registry.SetConsent("stub", true);
        var names = registry.Tools().Select(t => t.Name).ToArray();
        Assert.Contains("stub__echo", names);
        Assert.Contains("stub__boom", names);
        Assert.True(consent.IsEnabled("stub__echo"), "a consented tool must be enabled in ToolConsent");

        // A stopped server stops appearing, and takes its consent entries with it.
        await registry.StopAsync("stub");
        Assert.Empty(registry.Tools());
        Assert.False(consent.Snapshot().ContainsKey("stub__echo"));
    }

    [Fact]
    public async Task Read_only_hint_and_schema_survive_the_proxy()
    {
        if (!NodeAvailable()) return;                          // xunit 2.9.2 has no dynamic skip; see TODO.md
        await using var registry = NewRegistry(out _);
        registry.Add(StubSpec("stub") with { Consented = true });
        Assert.True(await registry.StartAsync("stub"));

        var echo = registry.Tools().Single(t => t.Name == "stub__echo");
        var boom = registry.Tools().Single(t => t.Name == "stub__boom");
        Assert.True(echo.ReadOnlyHint);
        Assert.False(boom.ReadOnlyHint);
        Assert.Equal("object", echo.InputSchema["type"]?.GetValue<string>());
        Assert.NotNull(echo.InputSchema["properties"]?["text"]);
    }

    [Fact]
    public async Task Tool_call_round_trips_through_the_listener_that_doca_talks_to()
    {
        if (!NodeAvailable()) return;                          // xunit 2.9.2 has no dynamic skip; see TODO.md
        await using var registry = NewRegistry(out var consent);
        registry.Add(StubSpec("stub") with { Consented = true });
        Assert.True(await registry.StartAsync("stub"));

        await using var listener = await StartListenerAsync(registry, consent);
        var url = listener.BoundUrl!;

        var listed = await RpcAsync(url, "tools/list", null);
        var names = listed?["result"]?["tools"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()).ToArray()
                    ?? Array.Empty<string?>();
        Assert.Contains("stub__echo", names);

        var called = await RpcAsync(url, "tools/call", new JsonObject
        {
            ["name"] = "stub__echo",
            ["arguments"] = new JsonObject { ["text"] = "hello desk" },
        });
        Assert.False(called?["result"]?["isError"]?.GetValue<bool>() ?? true);
        Assert.Equal("echo: hello desk", called?["result"]?["content"]?[0]?["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_tool_that_reports_a_failure_is_an_error_not_an_exception()
    {
        if (!NodeAvailable()) return;                          // xunit 2.9.2 has no dynamic skip; see TODO.md
        await using var registry = NewRegistry(out var consent);
        registry.Add(StubSpec("stub") with { Consented = true });
        Assert.True(await registry.StartAsync("stub"));

        await using var listener = await StartListenerAsync(registry, consent);
        var called = await RpcAsync(listener.BoundUrl!, "tools/call", new JsonObject { ["name"] = "stub__boom" });

        Assert.True(called?["result"]?["isError"]?.GetValue<bool>());
        Assert.Contains("on purpose", called?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "");
    }

    [Fact]
    public async Task An_unconsented_server_is_not_reachable_through_the_listener()
    {
        if (!NodeAvailable()) return;                          // xunit 2.9.2 has no dynamic skip; see TODO.md
        await using var registry = NewRegistry(out var consent);
        registry.Add(StubSpec("stub"));                       // Consented = false
        Assert.True(await registry.StartAsync("stub"));

        await using var listener = await StartListenerAsync(registry, consent);
        var called = await RpcAsync(listener.BoundUrl!, "tools/call", new JsonObject { ["name"] = "stub__echo" });

        // Not merely refused — never offered, so the listener does not know it.
        Assert.Equal(-32601, called?["error"]?["code"]?.GetValue<int>());
    }

    [Fact]
    public async Task Docas_own_client_can_call_a_server_this_machine_runs()
    {
        // The whole point of the feature, both hops at once: Doca's real client →
        // this listener → a local stdio server. Skipped where the server repo is absent.
        var clientJs = @"D:\doca\doca\DOCA\modules\mcp\client.js";
        if (!File.Exists(clientJs) || !NodeAvailable()) return;

        await using var registry = NewRegistry(out var consent);
        registry.Add(StubSpec("stub") with { Consented = true });
        Assert.True(await registry.StartAsync("stub"));

        await using var listener = await StartListenerAsync(registry, consent);

        var script = $$"""
            const { McpClient } = require({{ToJsString(clientJs)}});
            (async () => {
              const c = new McpClient({ id: 'desk-forward', transport: 'http', url: {{ToJsString(listener.BoundUrl!)}} });
              await c.start();
              const names = (c.tools || []).map(t => t.name);
              if (!names.includes('stub__echo')) throw new Error('forwarded tool missing: ' + names.join(','));
              const out = await c.callTool('stub__echo', { text: 'from doca' });
              if (out !== 'echo: from doca') throw new Error('unexpected reply: ' + out);
              console.log('OK');
            })().catch(e => { console.error(e); process.exit(1); });
            """;

        var (exitCode, stdout, stderr) = await RunNodeAsync(script);
        Assert.True(exitCode == 0, $"doca client could not reach the forwarded server: {stderr}{stdout}");
        Assert.Contains("OK", stdout);
    }

    /* ── Failure paths ─────────────────────────────────── */

    [Fact]
    public async Task A_command_that_does_not_exist_is_an_answer_not_a_throw()
    {
        await using var registry = NewRegistry(out _);
        registry.Add(new LocalMcpServerSpec { Id = "ghost", Command = "docadesk-no-such-binary-xyz" });

        Assert.False(await registry.StartAsync("ghost"));
        Assert.Equal(McpServerState.Error, registry.StateOf("ghost"));
        var view = registry.Views().Single(v => v.Spec.Id == "ghost");
        Assert.False(string.IsNullOrWhiteSpace(view.LastError));
        Assert.Equal(0, view.ToolCount);
    }

    [Fact]
    public async Task A_server_that_dies_during_the_handshake_keeps_its_stderr()
    {
        if (!NodeAvailable()) return;                          // xunit 2.9.2 has no dynamic skip; see TODO.md
        await using var registry = NewRegistry(out _);
        registry.Add(StubSpec("dying", "--fail"));

        Assert.False(await registry.StartAsync("dying"));
        Assert.Equal(McpServerState.Error, registry.StateOf("dying"));
        Assert.Contains(registry.LogOf("dying"), l => l.Contains("refusing to start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tool_that_never_answers_times_out_instead_of_hanging()
    {
        if (!NodeAvailable()) return;                          // xunit 2.9.2 has no dynamic skip; see TODO.md
        await using var registry = NewRegistry(out _, callTimeout: TimeSpan.FromSeconds(2));
        registry.Add(StubSpec("stub") with { Consented = true });
        Assert.True(await registry.StartAsync("stub"));

        var sleep = registry.Tools().Single(t => t.Name == "stub__sleep");
        var result = await sleep.CallAsync(new JsonObject(), "test", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_server_that_exits_mid_call_fails_the_call_and_is_marked_down()
    {
        if (!NodeAvailable()) return;                          // xunit 2.9.2 has no dynamic skip; see TODO.md
        await using var registry = NewRegistry(out _, callTimeout: TimeSpan.FromSeconds(20));
        registry.Add(StubSpec("stub") with { Consented = true });
        Assert.True(await registry.StartAsync("stub"));

        var quit = registry.Tools().Single(t => t.Name == "stub__quit");
        var result = await quit.CallAsync(new JsonObject(), "test", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(McpServerState.Error, registry.StateOf("stub"));
        Assert.Empty(registry.Tools());
    }

    /* ── Definitions ───────────────────────────────────── */

    [Fact]
    public async Task Definitions_survive_a_restart_of_the_app()
    {
        var store = Path.Combine(_dir, "mcp-servers.json");
        await using (var first = new LocalMcpRegistry(store, new ToolConsent()))
        {
            first.Add(new LocalMcpServerSpec
            {
                Id = "notes",
                Label = "Notes",
                Command = "node",
                Args = ["server.js", "--flag"],
                AutoStart = true,
                Consented = true,
            });
        }

        await using var second = new LocalMcpRegistry(store, new ToolConsent());
        var spec = second.List().Single();
        Assert.Equal("notes", spec.Id);
        Assert.Equal("Notes", spec.Label);
        Assert.Equal(["server.js", "--flag"], spec.Args);
        Assert.True(spec.AutoStart);
        Assert.True(spec.Consented);
    }

    [Fact]
    public async Task A_bad_definition_is_refused_before_anything_is_spawned()
    {
        await using var registry = NewRegistry(out _);
        registry.Add(new LocalMcpServerSpec { Id = "ok", Command = "node" });

        Assert.Throws<ArgumentException>(() => registry.Add(new LocalMcpServerSpec { Id = "Has Spaces", Command = "node" }));
        Assert.Throws<ArgumentException>(() => registry.Add(new LocalMcpServerSpec { Id = "nocmd", Command = "  " }));
        Assert.Throws<InvalidOperationException>(() => registry.Add(new LocalMcpServerSpec { Id = "ok", Command = "node" }));
        Assert.Single(registry.List());
    }

    [Fact]
    public void A_proxied_name_stays_inside_the_budget_doca_leaves_us()
    {
        // Doca presents ours as mcp__<desk>__<name>, and a function name may not
        // exceed 64 characters.
        var name = LocalMcpRegistry.ProxiedName("a-very-long-server-identifier-xy", "an_equally_long_tool_name_here");
        Assert.True(name.Length <= LocalMcpRegistry.MaxProxiedNameLength);
        Assert.Equal("filesystem__read_file", LocalMcpRegistry.ProxiedName("filesystem", "read_file"));
        Assert.Equal("fs__read_file", LocalMcpRegistry.ProxiedName("fs", "read/file"));
    }

    /// <summary>
    /// The real tool list of the Blender MCP server, which is where plain
    /// truncation stopped being a theory: every one of these is longer than the
    /// budget and the first five share a prefix well past it.
    /// </summary>
    private static readonly string[] BlenderTools =
    {
        "get_blendfile_summary_datablock_counts",
        "get_blendfile_summary_missing_files",
        "get_blendfile_summary_of_linked_libraries",
        "get_blendfile_summary_of_linked_libraries_for_cli",
        "get_blendfile_summary_path_info",
        "get_blendfile_summary_usage_guess",
        "get_screenshot_of_window_as_image",
        "get_screenshot_of_window_as_json",
        "jump_to_view3d_object_by_name",
        "jump_to_view3d_object_data_by_name",
        "execute_blender_code",
        "execute_blender_code_for_cli",
    };

    [Fact]
    public void Two_tools_never_arrive_under_one_name()
    {
        var names = BlenderTools.Select(t => LocalMcpRegistry.ProxiedName("blender", t)).ToArray();

        // Every one still fits the budget Doca leaves us...
        Assert.All(names, n => Assert.True(n.Length <= LocalMcpRegistry.MaxProxiedNameLength, n));

        // ...and no two of them are the same string. Before this, five pairs
        // collided and one of each pair could not be called at all.
        Assert.Equal(BlenderTools.Length, names.Distinct(StringComparer.Ordinal).Count());

        // The pair that made it obvious: identical for 40 characters, and the
        // suffix is the only thing telling them apart.
        var datablock = LocalMcpRegistry.ProxiedName("blender", "get_blendfile_summary_datablock_counts");
        var missing   = LocalMcpRegistry.ProxiedName("blender", "get_blendfile_summary_missing_files");
        Assert.NotEqual(datablock, missing);
    }

    [Fact]
    public void A_shortened_name_belongs_to_its_tool_and_not_to_its_position()
    {
        // The reason this is a hash of the name and not a _2 appended to a
        // duplicate: consent is keyed by the proxied name. If the name depended
        // on what else happened to be in the list, adding or removing an
        // unrelated tool would move a name onto a different tool, and a consent
        // the user granted to one would silently become a consent for another.
        var alone = LocalMcpRegistry.ProxiedName("blender", "get_blendfile_summary_missing_files");

        var crowded = new[] { "aaa", "get_blendfile_summary_datablock_counts", "zzz" }
            .Concat(BlenderTools)
            .Select(t => LocalMcpRegistry.ProxiedName("blender", t))
            .ToArray();

        Assert.Contains(alone, crowded);

        // Same input, same answer, every time — no per-process randomness, so a
        // restart cannot invalidate a consent list.
        Assert.Equal(alone, LocalMcpRegistry.ProxiedName("blender", "get_blendfile_summary_missing_files"));

        // And the server id still separates two servers offering one tool name.
        Assert.NotEqual(
            LocalMcpRegistry.ProxiedName("blender", "get_blendfile_summary_missing_files"),
            LocalMcpRegistry.ProxiedName("blendr",  "get_blendfile_summary_missing_files"));
    }

    /* ── Plumbing ──────────────────────────────────────── */

    private LocalMcpRegistry NewRegistry(out ToolConsent consent, TimeSpan? callTimeout = null)
    {
        consent = new ToolConsent();
        return new LocalMcpRegistry(Path.Combine(_dir, "mcp-servers.json"), consent, audit: null, callTimeout: callTimeout);
    }

    private async Task<McpHttpListener> StartListenerAsync(LocalMcpRegistry registry, ToolConsent consent)
    {
        var listener = new McpHttpListener(new McpListenerOptions
        {
            PathSecret = McpHttpListener.NewPathSecret(),
            Consent = consent,
            DynamicTools = registry.Tools,
            ToolCallTimeout = TimeSpan.FromSeconds(20),
        });
        await listener.StartLoopbackForTestsAsync(FreePort());
        return listener;
    }

    private static async Task<JsonNode?> RpcAsync(string url, string method, JsonNode? parameters)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
        };
        var res = await http.PostAsync(url, new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }

    private LocalMcpServerSpec StubSpec(string id, params string[] extraArgs)
    {
        var path = Path.Combine(_dir, "stub-mcp-server.js");
        if (!File.Exists(path)) File.WriteAllText(path, StubServerJs);
        return new LocalMcpServerSpec
        {
            Id = id,
            Label = id,
            Command = "node",
            Args = new[] { path }.Concat(extraArgs).ToArray(),
        };
    }

    private static string ToJsString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunNodeAsync(string script)
    {
        var path = Path.Combine(_dir, "probe-" + Guid.NewGuid().ToString("N") + ".js");
        await File.WriteAllTextAsync(path, script);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { path },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("node failed to start");
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
    }

    private static bool NodeAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(10_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>A real stdio MCP server, small enough to keep in the test.</summary>
    private const string StubServerJs = """
        'use strict';
        // A minimal stdio MCP server for DocaDesk's tests. --fail exits at once.
        if (process.argv.includes('--fail')) {
          process.stderr.write('refusing to start on purpose\n');
          process.exit(1);
        }
        process.stderr.write('stub mcp server up\n');

        const send = (obj) => process.stdout.write(JSON.stringify(obj) + '\n');
        const reply = (id, result) => send({ jsonrpc: '2.0', id, result });
        const text = (t, isError) => ({ content: [{ type: 'text', text: t }], isError: !!isError });

        const TOOLS = [
          {
            name: 'echo',
            description: 'Echo the text back.',
            inputSchema: { type: 'object', properties: { text: { type: 'string' } } },
            annotations: { readOnlyHint: true },
          },
          { name: 'boom', description: 'Always fails.', inputSchema: { type: 'object', properties: {} } },
          { name: 'sleep', description: 'Never answers.', inputSchema: { type: 'object', properties: {} } },
          { name: 'quit', description: 'Exits mid-call.', inputSchema: { type: 'object', properties: {} } },
        ];

        let buf = '';
        process.stdin.on('data', (chunk) => {
          buf += chunk.toString();
          let nl;
          while ((nl = buf.indexOf('\n')) >= 0) {
            const line = buf.slice(0, nl).trim();
            buf = buf.slice(nl + 1);
            if (!line) continue;
            let msg;
            try { msg = JSON.parse(line); } catch { continue; }
            if (msg.id === undefined) continue;                 // a notification

            if (msg.method === 'initialize') {
              reply(msg.id, {
                protocolVersion: '2025-06-18',
                capabilities: { tools: {} },
                serverInfo: { name: 'stub', version: '1.0.0' },
              });
            } else if (msg.method === 'tools/list') {
              reply(msg.id, { tools: TOOLS });
            } else if (msg.method === 'tools/call') {
              const name = msg.params && msg.params.name;
              const args = (msg.params && msg.params.arguments) || {};
              if (name === 'echo') reply(msg.id, text('echo: ' + (args.text || '')));
              else if (name === 'boom') reply(msg.id, text('it broke on purpose', true));
              else if (name === 'sleep') { /* deliberately no answer */ }
              else if (name === 'quit') process.exit(3);
              else send({ jsonrpc: '2.0', id: msg.id, error: { code: -32601, message: 'no such tool: ' + name } });
            } else {
              send({ jsonrpc: '2.0', id: msg.id, error: { code: -32601, message: 'no such method' } });
            }
          }
        });
        """;
}
