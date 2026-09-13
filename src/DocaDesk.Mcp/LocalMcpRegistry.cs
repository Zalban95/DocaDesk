using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocaDesk.Core.Audit;

namespace DocaDesk.Mcp;

/// <summary>
/// One MCP server this machine runs. A command, so only the person at the
/// keyboard adds one — nothing that arrives over the wire may define or edit it.
/// No environment block on purpose: that is where API keys would end up, and
/// this file is plain JSON.
/// </summary>
public sealed record LocalMcpServerSpec
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    public string? WorkingDirectory { get; init; }
    public bool AutoStart { get; init; }
    /// <summary>Until this is true the server's tools are not offered to anyone.</summary>
    public bool Consented { get; init; }
}

/// <summary>What the UI needs to draw one row.</summary>
public sealed record LocalMcpServerView(
    LocalMcpServerSpec Spec,
    McpServerState State,
    string? LastError,
    int ToolCount,
    IReadOnlyList<string> ToolNames);

/// <summary>
/// The servers DocaDesk runs on this machine, and the tools they contribute to
/// the listener Doca already talks to. Definitions live in one JSON file;
/// clients live in memory.
///
/// Two rules are copied from Doca's own registry on purpose: a server that fails
/// to start is an answer, not an exception — the panel wants to draw it — and
/// only a <see cref="McpServerState.Running"/> server contributes tools, which
/// is what makes the aggregated tool list dynamic.
/// </summary>
public sealed class LocalMcpRegistry : IAsyncDisposable
{
    /// <summary>Tool names reach Doca as mcp__&lt;desk&gt;__&lt;server&gt;__&lt;tool&gt;, and OpenAI caps a function name at 64.</summary>
    public const int MaxProxiedNameLength = 40;

    private static readonly Regex IdShape = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.Compiled);

    private readonly string _storePath;
    private readonly ToolConsent _consent;
    private readonly AuditLog? _audit;
    private readonly TimeSpan _callTimeout;
    private readonly object _gate = new();
    private readonly Dictionary<string, LocalMcpServerSpec> _specs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpStdioClient> _clients = new(StringComparer.Ordinal);

    public LocalMcpRegistry(string storePath, ToolConsent consent, AuditLog? audit = null, TimeSpan? callTimeout = null)
    {
        _storePath = storePath;
        _consent = consent;
        _audit = audit;
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(120);
        Load();
    }

    /// <summary>Raised when the tool list changes, so the offer to Doca can be refreshed.</summary>
    public event Action? ToolsChanged;

    public static string DefaultStorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DocaDesk",
        "mcp-servers.json");

    /* ── Definitions ───────────────────────────────────── */

    public IReadOnlyList<LocalMcpServerSpec> List()
    {
        lock (_gate) return _specs.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<LocalMcpServerView> Views()
    {
        return List().Select(spec =>
        {
            var client = Client(spec.Id);
            var names = client?.Tools.Select(t => ProxiedName(spec.Id, t.Name)).ToArray() ?? Array.Empty<string>();
            return new LocalMcpServerView(spec, client?.State ?? McpServerState.Stopped, client?.LastError, names.Length, names);
        }).ToArray();
    }

    public LocalMcpServerSpec Add(LocalMcpServerSpec spec)
    {
        var id = (spec.Id ?? "").Trim().ToLowerInvariant();
        if (!IdShape.IsMatch(id))
            throw new ArgumentException("An id must be 1–32 characters of a–z, 0–9 or '-', starting with a letter or digit.", nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.Command))
            throw new ArgumentException("A command is required.", nameof(spec));

        var stored = spec with
        {
            Id = id,
            Label = string.IsNullOrWhiteSpace(spec.Label) ? id : spec.Label.Trim(),
            Args = spec.Args?.Where(a => a is not null).ToArray() ?? Array.Empty<string>(),
        };

        lock (_gate)
        {
            if (_specs.ContainsKey(id)) throw new InvalidOperationException($"A server called '{id}' already exists.");
            _specs[id] = stored;
        }

        Save();
        _audit?.Add("mcp.local.add", $"{id}: {stored.Command} {string.Join(' ', stored.Args)}");
        return stored;
    }

    public async Task RemoveAsync(string id)
    {
        await StopAsync(id).ConfigureAwait(false);
        bool removed;
        lock (_gate) removed = _specs.Remove(id);
        if (!removed) return;
        Save();
        _audit?.Add("mcp.local.remove", id);
        ToolsChanged?.Invoke();
    }

    /// <summary>Consent is per server: its tools are offered only while it is on.</summary>
    public void SetConsent(string id, bool on)
    {
        LocalMcpServerSpec? spec;
        lock (_gate)
        {
            if (!_specs.TryGetValue(id, out spec)) return;
            _specs[id] = spec with { Consented = on };
        }

        Save();
        RegisterConsent(id, on);
        _audit?.Add("mcp.local.consent", $"{id} {(on ? "allowed" : "blocked")}");
        ToolsChanged?.Invoke();
    }

    public void SetAutoStart(string id, bool on)
    {
        lock (_gate)
        {
            if (!_specs.TryGetValue(id, out var spec)) return;
            _specs[id] = spec with { AutoStart = on };
        }
        Save();
    }

    /* ── Lifecycle ─────────────────────────────────────── */

    /// <summary>
    /// Starts a server. Returns false when it failed, with the reason in
    /// <see cref="LocalMcpServerView.LastError"/> and the detail in its log —
    /// a server that will not start is something to draw, not to throw.
    /// </summary>
    public async Task<bool> StartAsync(string id, CancellationToken ct = default)
    {
        LocalMcpServerSpec spec;
        lock (_gate)
        {
            if (!_specs.TryGetValue(id, out var found)) throw new KeyNotFoundException($"Unknown MCP server '{id}'.");
            spec = found;
        }

        var client = new McpStdioClient(spec.Id, spec.Command, spec.Args, spec.WorkingDirectory, _callTimeout);

        // Whatever this replaces has to be stopped, not just dropped. The guard
        // used to cover Running only, so a client still in its handshake was
        // overwritten and forgotten — nothing held it any more, so StopAsync,
        // StopAllAsync and DisposeAsync could never reach it and its child
        // process outlived the app. Two overlapping starts (a listener toggle
        // racing an mcp.listener push, or --mcp autostart racing either) left an
        // orphan holding the port the next attempt needed, reported to the user
        // as "did not start".
        McpStdioClient? displaced = null;
        lock (_gate)
        {
            if (_clients.TryGetValue(id, out var existing))
            {
                if (existing.State is McpServerState.Running or McpServerState.Starting) return true;
                displaced = existing;
            }
            _clients[id] = client;
        }
        if (displaced is not null)
        {
            try { await displaced.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _audit?.Add("mcp.local.replace.fail", $"{id}: {ex.Message}"); }
        }

        try
        {
            await client.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _audit?.Add("mcp.local.start.fail", $"{id}: {ex.Message}");
            ToolsChanged?.Invoke();
            return false;
        }

        RegisterConsent(id, spec.Consented);
        _audit?.Add("mcp.local.start", $"{id}: {client.Tools.Count} tool(s)");
        ToolsChanged?.Invoke();
        return true;
    }

    public async Task StopAsync(string id)
    {
        McpStdioClient? client;
        lock (_gate) _clients.Remove(id, out client);
        if (client is null) return;

        foreach (var tool in client.Tools) _consent.Remove(ProxiedName(id, tool.Name));
        await client.DisposeAsync().ConfigureAwait(false);
        _audit?.Add("mcp.local.stop", id);
        ToolsChanged?.Invoke();
    }

    public async Task StartAutoStartAsync(CancellationToken ct = default)
    {
        foreach (var spec in List().Where(s => s.AutoStart))
            await StartAsync(spec.Id, ct).ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        foreach (var id in List().Select(s => s.Id)) await StopAsync(id).ConfigureAwait(false);
    }

    public McpServerState StateOf(string id) => Client(id)?.State ?? McpServerState.Stopped;

    public IReadOnlyList<string> LogOf(string id) => Client(id)?.Log ?? Array.Empty<string>();

    /* ── Tools ─────────────────────────────────────────── */

    /// <summary>
    /// The tools of every consented, running server, named
    /// <c>&lt;serverId&gt;__&lt;tool&gt;</c>. Stopped servers contribute nothing,
    /// which is why this is a call and not a cached list.
    /// </summary>
    public IReadOnlyList<IMcpTool> Tools()
    {
        var tools = new List<IMcpTool>();
        foreach (var spec in List())
        {
            if (!spec.Consented) continue;
            var client = Client(spec.Id);
            if (client is null || client.State != McpServerState.Running) continue;
            foreach (var tool in client.Tools)
                tools.Add(new ProxiedTool(spec.Id, tool, client));
        }
        return tools;
    }

    /// <summary>
    /// The name one tool is presented under, <c>&lt;serverId&gt;__&lt;tool&gt;</c>,
    /// shortened to fit the budget without ever becoming another tool's name.
    ///
    /// Plain truncation was the bug. Blender's server offers
    /// <c>get_blendfile_summary_datablock_counts</c> and
    /// <c>get_blendfile_summary_missing_files</c> among others: they share more
    /// than forty characters of prefix, so five pairs arrived here as five
    /// identical names. The listener resolves a call with
    /// <c>FirstOrDefault(t =&gt; t.Name == name)</c>, so one of each pair answered
    /// for both and its twin could not be called at all — and
    /// <see cref="RegisterConsent"/> registered one key for the two of them, so
    /// allowing one allowed both. Observed against the real server, not imagined.
    ///
    /// The fix is a suffix derived from the full name rather than from its
    /// position in a list. That matters more than it looks: a de-duplicating pass
    /// numbering collisions <c>_2</c>, <c>_3</c> by the order tools arrive would
    /// hand the same name to a different tool when the server's list changes, and
    /// a consent the user granted to one would quietly become a consent for
    /// another. Hashing the full name keeps every tool's identity its own — the
    /// same tool is named the same thing on every machine, in every process, and
    /// after any restart.
    ///
    /// Names that already fit are untouched, so this only renames the tools that
    /// were broken. Their consent entries and any Doca-side "switched off" list
    /// refer to the old truncated name and will need granting once more, which is
    /// the price of them having been two tools under one name.
    /// </summary>
    public static string ProxiedName(string serverId, string toolName)
    {
        var slug = new string(toolName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        var name = $"{serverId}__{slug}";
        if (name.Length <= MaxProxiedNameLength) return name;

        var suffix = "_" + ShortHash(name);
        return name[..(MaxProxiedNameLength - suffix.Length)] + suffix;
    }

    /// <summary>
    /// Six hex characters of FNV-1a over the full name.
    ///
    /// Not for security — for identity. It has to be the same everywhere and
    /// forever, which rules out <see cref="string.GetHashCode()"/>: .NET
    /// randomises that per process, so a name built from it would change on every
    /// restart and take the consent list with it.
    /// </summary>
    private static string ShortHash(string value)
    {
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            var hash = offsetBasis;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
            {
                hash ^= b;
                hash *= prime;
            }
            return hash.ToString("x8")[..6];
        }
    }

    private McpStdioClient? Client(string id)
    {
        lock (_gate) return _clients.TryGetValue(id, out var c) ? c : null;
    }

    /// <summary>
    /// The listener asks <see cref="ToolConsent"/> for every call, so a proxied
    /// name has to exist there — registered enabled or disabled with its server.
    /// </summary>
    private void RegisterConsent(string id, bool on)
    {
        var client = Client(id);
        if (client is null) return;
        foreach (var tool in client.Tools) _consent.Register(ProxiedName(id, tool.Name), on);
    }

    /* ── Store ─────────────────────────────────────────── */

    private sealed class StoreFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("servers")] public List<LocalMcpServerSpec> Servers { get; set; } = new();
    }

    private static readonly JsonSerializerOptions StoreJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_storePath), StoreJson);
            lock (_gate)
            {
                _specs.Clear();
                foreach (var s in file?.Servers ?? new List<LocalMcpServerSpec>())
                {
                    if (string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Command)) continue;
                    _specs[s.Id] = s;
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt file is not a reason to lose the app; the .bak is still there.
            _audit?.Add("mcp.local.store.error", ex.Message);
        }
    }

    private void Save()
    {
        var file = new StoreFile { Servers = List().ToList() };
        var json = JsonSerializer.Serialize(file, StoreJson);
        var dir = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = _storePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_storePath)) File.Copy(_storePath, _storePath + ".bak", overwrite: true);
        File.Move(tmp, _storePath, overwrite: true);
    }

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}

/// <summary>One remote tool, presented to the listener as if it were local.</summary>
file sealed class ProxiedTool : IMcpTool
{
    private readonly McpStdioClient _client;
    private readonly McpRemoteTool _tool;

    public ProxiedTool(string serverId, McpRemoteTool tool, McpStdioClient client)
    {
        _tool = tool;
        _client = client;
        Name = LocalMcpRegistry.ProxiedName(serverId, tool.Name);
        Description = string.IsNullOrWhiteSpace(tool.Description)
            ? $"{tool.Name} on {serverId} (via DocaDesk)"
            : $"{tool.Description} (via DocaDesk → {serverId})";
    }

    public string Name { get; }
    public string Description { get; }
    public bool ReadOnlyHint => _tool.ReadOnly;
    public JsonObject InputSchema => _tool.InputSchema;

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
        => CallGuardedAsync(args, ct);

    private async Task<McpToolResult> CallGuardedAsync(JsonNode? args, CancellationToken ct)
    {
        if (_client.State != McpServerState.Running)
            return new McpToolResult { IsError = true, Text = $"{_client.Id} is not running." };
        try
        {
            return await _client.CallToolAsync(_tool.Name, args, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new McpToolResult { IsError = true, Text = $"Error: {ex.Message}" };
        }
    }
}
