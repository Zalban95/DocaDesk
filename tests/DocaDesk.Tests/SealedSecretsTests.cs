using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocaDesk.Mcp;

namespace DocaDesk.Tests;

/// <summary>
/// The device's half of a sealed secret (DOCA PROTOCOL.md §22.3, docs/api/sealed-secrets.md): opened with this
/// device's key, checked, used once as the hub said, never answered with the value, and the read tools held after.
/// The payloads are sealed here exactly as the hub's modules/sealed/seal.js does — AES-256-GCM, ciphertext ‖ tag,
/// additional data <c>doca-seal:&lt;device id&gt;</c>.
/// </summary>
public class SealedSecretsTests
{
    private const string Device = "dev_desk1";
    private const string Secret = "hunter2-🔑";
    private static readonly byte[] KeyBytes = RandomNumberGenerator.GetBytes(32);
    private static readonly SealKey Key = new(KeyBytes, $"doca-seal:{Device}");

    private sealed class FakeSink : ISecretSink
    {
        public readonly List<string> Calls = new();
        public int ClipsStopped;
        public Task TypeAsync(string value, CancellationToken ct) { Calls.Add($"type:{value}"); return Task.CompletedTask; }
        public Task<ISecretClip> ClipAsync(string value, CancellationToken ct) { Calls.Add($"clip:{value}"); return Task.FromResult<ISecretClip>(new Clip(this)); }
        public Task FieldAsync(string origin, int? tab, int reference, string value, CancellationToken ct) { Calls.Add($"field:{origin}:{reference}:{value}"); return Task.CompletedTask; }
        private sealed class Clip(FakeSink s) : ISecretClip { public Task StopAsync() { s.ClipsStopped++; return Task.CompletedTask; } }
    }

    private sealed class Clock { public DateTimeOffset Now = DateTimeOffset.UtcNow; }

    private static JsonObject Seal(object payload, byte[]? key = null, string? aad = null)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(payload);
        var iv = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key ?? KeyBytes, 16))
            gcm.Encrypt(iv, plain, cipher, tag, Encoding.UTF8.GetBytes(aad ?? Key.Aad));
        return new JsonObject { ["sealed"] = new JsonObject { ["v"] = 1, ["iv"] = Convert.ToBase64String(iv), ["data"] = Convert.ToBase64String(cipher.Concat(tag).ToArray()) } };
    }

    private static object Payload(string how, string? origin = null, string device = Device, long? iat = null, string? nonce = null, int? reference = null) => new
    {
        device,
        iat = iat ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        nonce = nonce ?? Guid.NewGuid().ToString("N"),
        how,
        value = Secret,
        @ref = reference,
        tab = (int?)null,
        origin,
        uses = 2,
        ttlSec = 30,
    };

    private static (SealedSecrets S, FakeSink Sink, Clock Clock) Make(string? gate = null, SealKey? key = null)
    {
        var sink = new FakeSink();
        var clock = new Clock();
        return (new SealedSecrets(() => key ?? Key, () => gate, sink, null, () => clock.Now), sink, clock);
    }

    [Fact]
    public async Task Typed_once_and_the_answer_never_carries_the_value()
    {
        var (s, sink, _) = Make();
        var r = await s.FillAsync(Seal(Payload("type")), default);
        Assert.False(r.IsError, r.Text);
        Assert.Equal(["type:" + Secret], sink.Calls);
        Assert.DoesNotContain("hunter2", r.Text);
        var answer = JsonNode.Parse(r.Text)!;
        Assert.Equal("typed", answer["done"]!.GetValue<string>());
        Assert.Equal(1, answer["uses"]!.GetValue<int>());
    }

    [Fact]
    public async Task Another_key_or_another_devices_aad_is_refused()
    {
        var (s, sink, _) = Make();
        var r = await s.FillAsync(Seal(Payload("type"), key: RandomNumberGenerator.GetBytes(32)), default);
        Assert.True(r.IsError);
        Assert.Equal("This was not sealed for this device: refused.", r.Text);
        r = await s.FillAsync(Seal(Payload("type"), aad: "doca-seal:dev_other"), default);
        Assert.Equal("This was not sealed for this device: refused.", r.Text);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task A_payload_for_another_device_is_refused()
    {
        var (s, sink, _) = Make();
        var r = await s.FillAsync(Seal(Payload("type", device: "dev_other")), default);
        Assert.True(r.IsError);
        Assert.Contains("another device", r.Text);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task An_old_payload_is_refused()
    {
        var (s, sink, _) = Make();
        var r = await s.FillAsync(Seal(Payload("type", iat: DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeMilliseconds())), default);
        Assert.True(r.IsError);
        Assert.Contains("too old", r.Text);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task A_nonce_is_used_once()
    {
        var (s, sink, _) = Make();
        var sealedOnce = Seal(Payload("type", nonce: "n-1"));
        Assert.False((await s.FillAsync(sealedOnce.DeepClone(), default)).IsError);
        var again = await s.FillAsync(sealedOnce.DeepClone(), default);
        Assert.True(again.IsError);
        Assert.Contains("already used", again.Text);
        Assert.Single(sink.Calls);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("clipboard")]
    public async Task A_secret_with_a_site_is_never_typed_or_pasted(string how)
    {
        var (s, sink, _) = Make();
        var r = await s.FillAsync(Seal(Payload(how, origin: "https://bank.example")), default);
        Assert.True(r.IsError);
        Assert.Contains("https://bank.example", r.Text);
        Assert.DoesNotContain("hunter2", r.Text);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task A_field_goes_only_to_its_origin_and_needs_one()
    {
        var (s, sink, _) = Make();
        var r = await s.FillAsync(Seal(Payload("field", origin: "https://bank.example", reference: 2)), default);
        Assert.False(r.IsError, r.Text);
        Assert.Equal(["field:https://bank.example:2:" + Secret], sink.Calls);
        Assert.Equal("field", JsonNode.Parse(r.Text)!["done"]!.GetValue<string>());

        var none = await s.FillAsync(Seal(Payload("field")), default);
        Assert.True(none.IsError);
        Assert.Single(sink.Calls);
    }

    [Fact]
    public async Task The_read_tools_wait_sixty_seconds_after_a_use()
    {
        var (s, _, clock) = Make();
        Assert.Null(s.Blocks("shell"));
        await s.FillAsync(Seal(Payload("type")), default);
        foreach (var read in new[] { "shell", "shell_job", "files_read", "screen_capture", "screenshot", "get_clipboard_text" })
            Assert.Contains("waits", s.Blocks(read));
        Assert.Null(s.Blocks("input_click"));
        Assert.Null(s.Blocks("files_list"));
        clock.Now += TimeSpan.FromSeconds(59);
        Assert.NotNull(s.Blocks("shell"));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Null(s.Blocks("shell"));
    }

    [Fact]
    public async Task On_the_clipboard_the_reads_wait_until_it_is_gone_and_it_is_taken_off()
    {
        var (s, sink, clock) = Make();
        var r = await s.FillAsync(Seal(Payload("clipboard")), default);
        Assert.False(r.IsError, r.Text);
        var answer = JsonNode.Parse(r.Text)!;
        Assert.Equal("clipboard", answer["done"]!.GetValue<string>());
        Assert.False(answer["counted"]!.GetValue<bool>());   // Windows cannot count pastes
        Assert.Equal(2, answer["uses"]!.GetValue<int>());
        Assert.True(s.OnClipboard);
        clock.Now += TimeSpan.FromMinutes(2);   // past the read hold: the clipboard alone still holds them
        Assert.Contains("clipboard", s.Blocks("get_clipboard_text"));
        await s.DisarmAsync();
        Assert.False(s.OnClipboard);
        Assert.Equal(1, sink.ClipsStopped);
        Assert.Null(s.Blocks("get_clipboard_text"));
    }

    [Fact]
    public async Task Without_the_family_or_the_key_nothing_is_used()
    {
        var (s, sink, _) = Make(gate: "This machine does not lend its input family.");
        var r = await s.FillAsync(Seal(Payload("type")), default);
        Assert.True(r.IsError);
        Assert.Contains("input family", r.Text);

        var noKey = new SealedSecrets(() => null, () => null, sink);
        r = await noKey.FillAsync(Seal(Payload("type")), default);
        Assert.True(r.IsError);
        Assert.Contains("seal key", r.Text);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void The_key_is_stored_and_read_back_with_its_device()
    {
        var back = SealKey.FromStored(Key.ToStored());
        Assert.NotNull(back);
        Assert.Equal(Device, back!.DeviceId);
        Assert.Equal(KeyBytes, back.Key);
        Assert.Null(SealKey.FromStored("not json"));
    }

    [Fact]
    public async Task The_hidden_tool_is_never_listed_and_the_hold_reaches_the_server()
    {
        var (s, _, _) = Make();
        var shell = new StubTool("shell");
        var d = new McpDispatcher(new McpListenerOptions { PathSecret = "x", Tools = [shell], Sealed = s, Consent = Consented("shell") });

        var list = await d.DispatchAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", "test");
        Assert.DoesNotContain(SealedSecrets.ToolName, list);
        Assert.Contains("\"shell\"", list);

        var call = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = SealedSecrets.ToolName, ["arguments"] = Seal(Payload("type")) } };
        var used = await d.DispatchAsync(call.ToJsonString(), "test");
        Assert.Contains("typed", used);
        Assert.DoesNotContain("hunter2", used);

        var held = await d.DispatchAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"shell","arguments":{}}}""", "test");
        Assert.Contains("\"isError\":true", held);
        Assert.Contains("A secret was just used", held);
        Assert.Equal(0, shell.Calls);
    }

    private static ToolConsent Consented(string name) { var c = new ToolConsent(); c.Register(name, true); return c; }

    private sealed class StubTool(string name) : IMcpTool
    {
        public int Calls;
        public string Name => name;
        public string Description => "stub";
        public bool ReadOnlyHint => false;
        public JsonObject InputSchema => new() { ["type"] = "object" };
        public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct) { Calls++; return Task.FromResult(new McpToolResult { Text = "ran" }); }
    }
}

/// <summary>
/// The real Windows clipboard path. It replaces what the person has copied, so it runs only when asked
/// (<c>DOCADESK_CLIPBOARD_TEST=1</c>) and otherwise returns early, like the suite's other environment guards.
/// </summary>
public class WindowsSecretClipboardTests
{
    [Fact]
    public async Task Set_on_the_clipboard_then_cleared_only_while_it_still_holds_it()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("DOCADESK_CLIPBOARD_TEST") != "1") return;
        var sink = new WindowsSecretSink(() => null);
        var value = "docadesk-test-" + Guid.NewGuid().ToString("N");

        var clip = await sink.ClipAsync(value, default);
        Assert.Equal(value, Clipboard());
        await clip.StopAsync();
        Assert.Equal("", Clipboard());

        // Something copied since is the person's: left alone.
        clip = await sink.ClipAsync(value, default);
        SetClipboard("the person's own");
        await clip.StopAsync();
        Assert.Equal("the person's own", Clipboard());
    }

    private static string Clipboard() => Ps("Get-Clipboard -Raw");
    private static void SetClipboard(string text) => Ps($"Set-Clipboard -Value '{text.Replace("'", "''")}'");

    private static string Ps(string command)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command}\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.TrimEnd('\r', '\n');
    }
}
