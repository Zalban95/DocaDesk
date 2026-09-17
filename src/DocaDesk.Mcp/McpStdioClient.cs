using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocaDesk.Mcp;

public enum McpServerState
{
    Stopped,
    Starting,
    Running,
    Error,
}

/// <summary>A tool as the remote server declares it.</summary>
public sealed record McpRemoteTool(string Name, string Description, JsonObject InputSchema, bool ReadOnly);

/// <summary>
/// An MCP client over a child process's stdio: newline-delimited JSON-RPC 2.0.
///
/// Deliberately the same dialect as Doca's <c>modules/mcp/client.js</c> — same
/// protocol version, same handshake order, same timeouts, same treatment of
/// non-text content blocks — so a server behaves identically whether Doca runs
/// it on the host or DocaDesk runs it on this machine.
///
/// A server logs to stderr, so stderr is kept in a ring buffer and surfaced as
/// the server's log instead of being swallowed.
/// </summary>
public sealed class McpStdioClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";
    private const int LogLines = 200;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly List<string> _log = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();

    private Process? _child;
    private int _nextId = 1;

    public McpStdioClient(
        string id,
        string command,
        IReadOnlyList<string>? args = null,
        string? workingDirectory = null,
        TimeSpan? callTimeout = null)
    {
        Id = id;
        Command = command;
        Args = args ?? Array.Empty<string>();
        WorkingDirectory = workingDirectory;
        CallTimeout = callTimeout ?? TimeSpan.FromSeconds(120);
    }

    public string Id { get; }
    public string Command { get; }
    public IReadOnlyList<string> Args { get; }
    public string? WorkingDirectory { get; }
    public TimeSpan CallTimeout { get; }

    public McpServerState State { get; private set; } = McpServerState.Stopped;
    public string? LastError { get; private set; }
    public string? ServerName { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }

    private volatile IReadOnlyList<McpRemoteTool> _tools = Array.Empty<McpRemoteTool>();
    public IReadOnlyList<McpRemoteTool> Tools => _tools;

    public IReadOnlyList<string> Log
    {
        get { lock (_gate) return _log.ToArray(); }
    }

    /// <summary>Spawn the server, complete the handshake and cache its tools.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (State is McpServerState.Running or McpServerState.Starting) return;

        State = McpServerState.Starting;
        LastError = null;
        lock (_gate) _log.Clear();

        try
        {
            Spawn();

            var info = await RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = DocaDesk.Core.DocaDeskConstants.ClientName,
                    ["version"] = DocaDesk.Core.DocaDeskConstants.ClientVersion,
                },
            }, HandshakeTimeout, ct).ConfigureAwait(false);

            ServerName = info?["serverInfo"]?["name"]?.GetValue<string>();
            Notify("notifications/initialized", null);
            await ListToolsAsync(ct).ConfigureAwait(false);

            State = McpServerState.Running;
            StartedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Note($"[error] {ex.Message}");
            await StopAsync(quiet: true).ConfigureAwait(false);
            // The real reason ("No space left on device", a Python traceback) is
            // usually the server's last stderr line; put it where the row shows it.
            var stderr = Log.Where(l => !l.StartsWith('[')).ToArray();
            var lastStderr = stderr.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                ?? stderr.LastOrDefault();
            if (lastStderr is not null) LastError = $"{ex.Message} — {Truncate(lastStderr.Trim(), 300)}";
            State = McpServerState.Error;
            throw;
        }
    }

    public async Task<IReadOnlyList<McpRemoteTool>> ListToolsAsync(CancellationToken ct = default)
    {
        var res = await RequestAsync("tools/list", new JsonObject(), HandshakeTimeout, ct).ConfigureAwait(false);
        var list = new List<McpRemoteTool>();
        if (res?["tools"] is JsonArray arr)
        {
            foreach (var t in arr)
            {
                var name = t?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var schema = t?["inputSchema"]?.DeepClone() as JsonObject
                    ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                list.Add(new McpRemoteTool(
                    name!,
                    t?["description"]?.GetValue<string>() ?? "",
                    schema,
                    // Absent `readOnlyHint`, assume a tool can change something.
                    t?["annotations"]?["readOnlyHint"]?.GetValue<bool>() ?? false));
            }
        }
        _tools = list;
        return list;
    }

    /// <summary>
    /// Call a tool and flatten the reply the way the model wants it: text blocks
    /// joined, anything else named rather than dropped, so "[image]" still tells
    /// the model something came back that it cannot see.
    /// </summary>
    public async Task<McpToolResult> CallToolAsync(string name, JsonNode? args, CancellationToken ct = default)
    {
        var res = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = args?.DeepClone() ?? new JsonObject(),
        }, CallTimeout, ct).ConfigureAwait(false);

        var parts = new List<string>();
        if (res?["content"] is JsonArray content)
        {
            foreach (var block in content)
            {
                var type = block?["type"]?.GetValue<string>() ?? "unknown";
                parts.Add(type == "text" ? block?["text"]?.GetValue<string>() ?? "" : $"[{type}]");
            }
        }

        var text = string.Join('\n', parts).Trim();
        var isError = res?["isError"]?.GetValue<bool>() ?? false;
        return new McpToolResult
        {
            IsError = isError,
            Text = isError ? $"Error: {(text.Length > 0 ? text : "the tool reported a failure")}" : (text.Length > 0 ? text : "(no output)"),
        };
    }

    public async Task StopAsync(bool quiet = false)
    {
        var child = _child;
        _child = null;
        State = McpServerState.Stopped;
        _tools = Array.Empty<McpRemoteTool>();
        StartedAt = null;
        if (!quiet) LastError = null;

        if (child is not null)
        {
            try
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
            try { await child.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(4)).Token).ConfigureAwait(false); }
            catch { /* took too long or already reaped */ }
            child.Dispose();
        }

        FailAll("stopped");
    }

    private void Spawn()
    {
        // Process.Start reports a bad directory as the same Win32Exception as a
        // missing command, which blamed the command.
        if (!string.IsNullOrWhiteSpace(WorkingDirectory) && !Directory.Exists(WorkingDirectory))
            throw new InvalidOperationException($"working directory is not a folder that exists: {WorkingDirectory}");

        var psi = new ProcessStartInfo
        {
            FileName = Command,
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in Args) psi.ArgumentList.Add(a);

        Process child;
        try
        {
            child = Process.Start(psi) ?? throw new InvalidOperationException($"{Command}: could not start");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"{Command}: not found ({ex.Message})", ex);
        }

        _child = child;
        child.EnableRaisingEvents = true;
        child.Exited += (_, _) =>
        {
            Note($"[exit] code {TryExitCode(child)}");
            // An exit we asked for is not a failure; one we did not is.
            if (State != McpServerState.Stopped)
            {
                State = McpServerState.Error;
                LastError ??= $"exited (code {TryExitCode(child)})";
                _tools = Array.Empty<McpRemoteTool>();
            }
            FailAll("server exited");
        };

        _ = Task.Run(() => ReadStdoutAsync(child));
        _ = Task.Run(() => ReadStderrAsync(child));
    }

    private static string TryExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); }
        catch { return "?"; }
    }

    /// <summary>
    /// Drain the server's stdout until it stops talking — and say so when it does.
    ///
    /// Two ways out of here used to leave the object claiming to be healthy: the
    /// child closing stdout without exiting (so `Exited` never fires and the
    /// handler that records it never runs), and any exception that is not a
    /// JsonException escaping the inner catch — `GetValue&lt;string&gt;()` on a
    /// `method` or an `error.message` that is not a string throws
    /// InvalidOperationException, not JsonException. Either way `State` stayed
    /// `Running`, the panel kept drawing "running · N tools", and every later
    /// call wrote into a pipe nobody was reading and waited the full call
    /// timeout. Two minutes per call, and a green light in the UI.
    /// </summary>
    private async Task ReadStdoutAsync(Process child)
    {
        try
        {
            while (!child.StandardOutput.EndOfStream)
            {
                var line = await child.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (line.Trim().Length == 0) continue;
                // One malformed message costs one line, not the connection.
                try { OnMessage(JsonNode.Parse(line)); }
                catch (Exception ex) { Note($"[unreadable line] {ex.Message}: {Truncate(line, 200)}"); }
            }
        }
        catch (Exception ex) { Note($"[stdout] {ex.Message}"); }
        finally
        {
            // Reaching here means this server can no longer answer anything.
            if (State == McpServerState.Running)
            {
                State = McpServerState.Error;
                LastError ??= "the server stopped writing to stdout";
                _tools = Array.Empty<McpRemoteTool>();
            }
            FailAll("the server stopped writing to stdout");
        }
    }

    private async Task ReadStderrAsync(Process child)
    {
        try
        {
            while (!child.StandardError.EndOfStream)
            {
                var line = await child.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                Note(line);
            }
        }
        catch { /* the process went away; the exit handler reports it */ }
    }

    /// <summary>A server may call us too. We implement nothing, so answer rather than hang.</summary>
    private void OnMessage(JsonNode? msg)
    {
        if (msg is null) return;
        var hasId = msg["id"] is not null;
        var method = msg["method"]?.GetValue<string>();

        if (hasId && !string.IsNullOrEmpty(method))
        {
            var reply = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = msg["id"]!.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"{method} is not supported by this client" },
            };
            _ = WriteLineAsync(reply.ToJsonString());
            return;
        }

        if (!hasId) return;                                   // a notification, nothing to do
        if (msg["id"]!.GetValueKind() != JsonValueKind.Number) return;

        var id = msg["id"]!.GetValue<int>();
        TaskCompletionSource<JsonNode?>? pending;
        lock (_gate)
        {
            if (!_pending.Remove(id, out pending)) return;
        }

        if (msg["error"] is JsonNode err)
            pending!.TrySetException(new InvalidOperationException(err["message"]?.GetValue<string>() ?? "JSON-RPC error"));
        else
            pending!.TrySetResult(msg["result"]?.DeepClone());
    }

    private async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken ct)
    {
        if (_child is null) throw new InvalidOperationException("server is not running");

        int id;
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            id = _nextId++;
            _pending[id] = tcs;
        }

        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters?.DeepClone() ?? new JsonObject(),
        };

        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, ct);
        await using var _ = linked.Token.Register(() =>
        {
            lock (_gate) _pending.Remove(id);
            tcs.TrySetException(timer.IsCancellationRequested
                ? new TimeoutException($"{method} timed out after {timeout.TotalSeconds:0.#}s")
                : new OperationCanceledException(ct));
        }).ConfigureAwait(false);

        try
        {
            await WriteLineAsync(envelope.ToJsonString()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate) _pending.Remove(id);
            throw new InvalidOperationException($"could not reach {Id}: {ex.Message}", ex);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private void Notify(string method, JsonNode? parameters)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters?.DeepClone() ?? new JsonObject(),
        };
        _ = WriteLineAsync(envelope.ToJsonString());
    }

    private async Task WriteLineAsync(string json)
    {
        var child = _child ?? throw new InvalidOperationException("server is not running");
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await child.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
            await child.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private void FailAll(string reason)
    {
        List<TaskCompletionSource<JsonNode?>> waiting;
        lock (_gate)
        {
            waiting = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var w in waiting) w.TrySetException(new InvalidOperationException(reason));
    }

    private void Note(string line)
    {
        lock (_gate)
        {
            foreach (var l in line.Split('\n'))
            {
                if (l.Trim().Length > 0) _log.Add(l.TrimEnd());
            }
            if (_log.Count > LogLines) _log.RemoveRange(0, _log.Count - LogLines);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public async ValueTask DisposeAsync()
    {
        await StopAsync(quiet: true).ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
