using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocaDesk.Core.Audit;

namespace DocaDesk.Mcp;

/// <summary>The key the hub seals secrets for this device with (<c>GET /api/v1/mcp/self/seal</c>).</summary>
public sealed record SealKey(byte[] Key, string Aad)
{
    /// <summary>This device's id, as the hub bound it: the additional data is <c>doca-seal:&lt;device id&gt;</c>.</summary>
    public string DeviceId => Aad.StartsWith("doca-seal:", StringComparison.Ordinal) ? Aad["doca-seal:".Length..] : Aad;

    /// <summary>The stored form (DPAPI): the key and the additional data, never logged.</summary>
    public string ToStored() => JsonSerializer.Serialize(new { key = Convert.ToBase64String(Key), aad = Aad });

    public static SealKey? FromStored(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try
        {
            var o = JsonNode.Parse(stored);
            var key = Convert.FromBase64String(o?["key"]?.GetValue<string>() ?? "");
            var aad = o?["aad"]?.GetValue<string>() ?? "";
            return key.Length == 32 && aad.Length > 0 ? new SealKey(key, aad) : null;
        }
        catch { return null; }
    }
}

/// <summary>A refusal to use a sealed secret: a sentence for the agent, never containing the value.</summary>
public sealed class SecretRefusedException(string message) : Exception(message);

/// <summary>A secret on the clipboard: taking it off clears the clipboard only if it still holds the secret.</summary>
public interface ISecretClip
{
    Task StopAsync();
}

/// <summary>A web page's field the desk can fill: the panel DocaDesk shows (WebView2). Throws <see cref="SecretRefusedException"/>.</summary>
public interface ISecretField
{
    Task FillAsync(string origin, int? tab, int reference, string value, CancellationToken ct);
}

/// <summary>How this machine uses a secret. Windows: <see cref="WindowsSecretSink"/>; the tests a fake.</summary>
public interface ISecretSink
{
    Task TypeAsync(string value, CancellationToken ct);
    Task<ISecretClip> ClipAsync(string value, CancellationToken ct);
    Task FieldAsync(string origin, int? tab, int reference, string value, CancellationToken ct);
}

/// <summary>
/// The device's half of a sealed secret (DOCA's PROTOCOL.md §22.3, docs/api/sealed-secrets.md; TODO P1.3). The hub
/// hands this machine a password, a PIN or a key for one use, sealed with the key it gave this device alone, to the
/// hidden tool <c>secret_fill</c> — never listed in tools/list, so no agent can call it. Opened here, checked (for this
/// device, recent, never seen before), used once as the hub said, and forgotten; the answer says what was done, never
/// the value.
///
/// A secret with an <c>origin</c> belongs to a site and goes only into a credential field on exactly that site — here,
/// the panel's WebView2 — and is never typed or pasted. While a secret is on the clipboard, and for
/// <see cref="ReadHold"/> after any use, this machine refuses its read tools (<see cref="Reads"/>): a value just typed,
/// pasted or filled could otherwise be read straight back (security review 2026-10-07).
/// </summary>
public sealed class SealedSecrets
{
    public const string ToolName = "secret_fill";
    public static readonly TimeSpan ReadHold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NonceMemory = TimeSpan.FromMinutes(10);

    /// <summary>What reads back what was typed, pasted or filled: command lines, file reads, the screen, the clipboard.</summary>
    public static readonly HashSet<string> Reads = new(StringComparer.Ordinal)
    {
        "shell", "shell_job", "elevated_run", "processes_start", "files_read",
        "screen_capture", "screenshot", "get_clipboard_text", "device_clipboard_read",
    };

    private readonly Func<SealKey?> _key;
    private readonly Func<string?> _gate;
    private readonly ISecretSink _sink;
    private readonly AuditLog? _audit;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private DateTimeOffset _heldUntil = DateTimeOffset.MinValue;
    private Armed? _armed;

    private sealed class Armed
    {
        public required ISecretClip Clip { get; init; }
        public required DateTimeOffset Until { get; init; }
        public CancellationTokenSource Timer { get; } = new();
    }

    /// <param name="key">The seal key, null until it has been taken from the hub.</param>
    /// <param name="gate">Null when this machine may use a secret now, else the sentence that says why not.</param>
    public SealedSecrets(Func<SealKey?> key, Func<string?> gate, ISecretSink sink, AuditLog? audit = null, Func<DateTimeOffset>? now = null)
    {
        _key = key;
        _gate = gate;
        _sink = sink;
        _audit = audit;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The opened payload — <c>Value</c> is the secret.</summary>
    public sealed class Payload
    {
        public string Device { get; init; } = "";
        public long Iat { get; init; }
        public string Nonce { get; init; } = "";
        public string How { get; init; } = "";
        public string Value { get; set; } = "";
        public int? Ref { get; init; }
        public int? Tab { get; init; }
        public string? Origin { get; init; }
        public int Uses { get; init; } = 1;
        public int TtlSec { get; init; } = 30;
    }

    /// <summary>Open and check a sealed payload; throws a sentence (never the value) when it is not for here and now.</summary>
    public Payload Open(JsonNode? sealedNode)
    {
        var key = _key() ?? throw new SecretRefusedException(
            "This machine has not taken its seal key from the hub yet: reconnect DocaDesk (Settings → Server → Reconnect) and try again.");
        JsonNode? p;
        try
        {
            var iv = Convert.FromBase64String(sealedNode?["iv"]?.GetValue<string>() ?? "");
            var data = Convert.FromBase64String(sealedNode?["data"]?.GetValue<string>() ?? "");
            if (iv.Length != 12 || data.Length < 16) throw new CryptographicException();
            var cipher = data.AsSpan(0, data.Length - 16);
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(key.Key, 16))
                gcm.Decrypt(iv, cipher, data.AsSpan(data.Length - 16), plain, Encoding.UTF8.GetBytes(key.Aad));
            p = JsonNode.Parse(plain);
            CryptographicOperations.ZeroMemory(plain);
        }
        catch { throw new SecretRefusedException("This was not sealed for this device: refused."); }

        var payload = new Payload
        {
            Device = Str(p, "device") ?? "",
            Iat = Num(p, "iat") ?? 0,
            Nonce = Str(p, "nonce") ?? "",
            How = Str(p, "how") ?? "",
            Value = Str(p, "value") ?? "",
            Ref = (int?)Num(p, "ref"),
            Tab = (int?)Num(p, "tab"),
            Origin = Str(p, "origin") is { Length: > 0 } o ? o : null,
            Uses = (int)Math.Clamp(Num(p, "uses") ?? 1, 1, 10),
            TtlSec = (int)Math.Clamp(Num(p, "ttlSec") ?? 30, 5, 300),
        };
        if (payload.Device != key.DeviceId)
            throw new SecretRefusedException("This was sealed for another device: refused.");
        var now = _now();
        if (Math.Abs((now - DateTimeOffset.FromUnixTimeMilliseconds(payload.Iat)).TotalMilliseconds) >= MaxAge.TotalMilliseconds)
            throw new SecretRefusedException("This sealed secret is too old (or this machine's clock is minutes off): refused.");
        lock (_lock)
        {
            foreach (var gone in _seen.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) _seen.Remove(gone);
            if (payload.Nonce.Length == 0 || _seen.ContainsKey(payload.Nonce))
                throw new SecretRefusedException("This sealed secret was already used: refused.");
            _seen[payload.Nonce] = now + NonceMemory;
        }
        return payload;
    }

    /// <summary>The hidden tool. Answers <c>{done, uses, counted, seconds}</c> as text — never the value.</summary>
    public async Task<McpToolResult> FillAsync(JsonNode? args, CancellationToken ct)
    {
        Payload? p = null;
        try
        {
            if (_gate() is { } refused) throw new SecretRefusedException(refused);
            p = Open(args?["sealed"]);
            var where = p.Origin is null ? p.How : $"{p.How} on {p.Origin}";
            object answer;
            switch (p.How)
            {
                case "field":
                    if (p.Origin is null)
                        throw new SecretRefusedException("A secret goes into a web page's field only on its own site, and this one has none: refused.");
                    Hold();
                    await _sink.FieldAsync(p.Origin, p.Tab, p.Ref ?? 0, p.Value, ct).ConfigureAwait(false);
                    answer = new { done = "field", uses = 1, counted = true, seconds = 0 };
                    break;
                case "type":
                    RefuseSiteSecret(p);
                    Hold();
                    await _sink.TypeAsync(p.Value, ct).ConfigureAwait(false);
                    answer = new { done = "typed", uses = 1, counted = true, seconds = 0 };
                    break;
                case "clipboard":
                    RefuseSiteSecret(p);
                    await DisarmAsync().ConfigureAwait(false);   // one secret on the clipboard at a time
                    Hold();
                    var clip = await _sink.ClipAsync(p.Value, ct).ConfigureAwait(false);
                    Arm(clip, TimeSpan.FromSeconds(p.TtlSec));
                    answer = new { done = "clipboard", uses = p.Uses, counted = false, seconds = p.TtlSec };
                    break;
                default:
                    throw new SecretRefusedException($"This device does not know how to use a secret as \"{p.How}\": refused.");
            }
            _audit?.Add("secret.use", where);   // how and where, never the value
            return new McpToolResult { Text = JsonSerializer.Serialize(answer) };
        }
        catch (SecretRefusedException ex)
        {
            _audit?.Add("secret.refused", ex.Message);
            return new McpToolResult { IsError = true, Text = ex.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An OS failure's message could in principle echo what it was handed; say only what failed.
            _audit?.Add("secret.failed", ex.GetType().Name);
            return new McpToolResult { IsError = true, Text = $"Could not use the secret on this device ({ex.GetType().Name})." };
        }
        finally
        {
            if (p is not null) p.Value = "";   // forget it
        }
    }

    private static void RefuseSiteSecret(Payload p)
    {
        if (p.Origin is not null)
            throw new SecretRefusedException(
                $"This secret belongs to {p.Origin} and goes only into that site's own field: it is not typed or pasted on this machine.");
    }

    private void Hold()
    {
        lock (_lock) _heldUntil = _now() + ReadHold;   // before the use: a read racing it waits too
    }

    private void Arm(ISecretClip clip, TimeSpan ttl)
    {
        var armed = new Armed { Clip = clip, Until = _now() + ttl };
        lock (_lock) _armed = armed;
        _ = Task.Delay(ttl, armed.Timer.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            bool mine;
            lock (_lock) { mine = ReferenceEquals(_armed, armed); if (mine) _armed = null; }
            if (mine) { try { await clip.StopAsync().ConfigureAwait(false); } catch { /* the clipboard was busy */ } }
        }, TaskScheduler.Default);
    }

    /// <summary>Take a secret off the clipboard now (a newer one, or quitting). The read hold still runs out on its own.</summary>
    public async Task DisarmAsync()
    {
        Armed? a;
        lock (_lock) { a = _armed; _armed = null; }
        if (a is null) return;
        a.Timer.Cancel();
        try { await a.Clip.StopAsync().ConfigureAwait(false); } catch { /* the clipboard was busy */ }
    }

    /// <summary>True while a secret sits on the clipboard.</summary>
    public bool OnClipboard { get { lock (_lock) return _armed is not null; } }

    /// <summary>What this machine refuses <paramref name="tool"/> now (a sentence), or null.</summary>
    public string? Blocks(string tool)
    {
        if (!Reads.Contains(tool)) return null;
        var now = _now();
        lock (_lock)
        {
            if (_armed is not null)
                return $"A secret is on this machine's clipboard for {Math.Max(1, Math.Ceiling((_armed.Until - now).TotalSeconds))} s more; {tool} waits until it is gone.";
            if (now < _heldUntil)
                return $"A secret was just used on this machine: {tool} waits {Math.Ceiling((_heldUntil - now).TotalSeconds)} s more.";
        }
        return null;
    }

    private static string? Str(JsonNode? o, string k)
    {
        try { return o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; } catch { return null; }
    }

    private static long? Num(JsonNode? o, string k)
    {
        try
        {
            if (o?[k] is not JsonValue v) return null;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            return null;
        }
        catch { return null; }
    }
}
