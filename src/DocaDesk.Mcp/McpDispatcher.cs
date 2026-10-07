using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocaDesk.Mcp;

/// <summary>
/// The MCP server itself, apart from how its messages travel: JSON-RPC in, JSON-RPC out —
/// <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c>, <c>ping</c>, with the consent gate on every call. The HTTP
/// listener (<see cref="McpHttpListener"/>) and the socket this PC opens to the hub (PROTOCOL.md §22.2) both carry it,
/// so a tool behaves the same whichever way the hub reaches it.
/// </summary>
public sealed class McpDispatcher
{
    private readonly McpListenerOptions _opt;

    public McpDispatcher(McpListenerOptions options) => _opt = options;

    public async Task<string> DispatchAsync(string body, string via)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch { return Err(null, -32700, "parse error"); }

        var id = root?["id"]?.DeepClone();
        var method = root?["method"]?.GetValue<string>();
        var parameters = root?["params"];
        if (string.IsNullOrEmpty(method))
            return Err(id, -32600, "invalid request");

        try
        {
            object? result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2025-06-18",
                    // listChanged: DOCA then holds the GET stream open and hears NotifyToolsChanged.
                    capabilities = new { tools = new { listChanged = true } },
                    serverInfo = new { name = "DocaDesk", version = "0.1.0" },
                },
                "tools/list" => new { tools = ListTools() },
                "tools/call" => await CallToolAsync(parameters, via).ConfigureAwait(false),
                "ping" => new { },
                _ => throw new McpRpcException(-32601, $"Method not found: {method}"),
            };

            var ok = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id is null ? null : JsonNode.Parse(id.ToJsonString()),
                ["result"] = JsonSerializer.SerializeToNode(result),
            };
            return ok.ToJsonString();
        }
        catch (McpRpcException rex)
        {
            return Err(id, rex.Code, rex.Message);
        }
        catch (Exception ex)
        {
            // Keeping only ex.Message discards the one thing that identifies an
            // unexpected failure — and some exceptions carry no message at all.
            // A clipboard call once reached the agent as
            // {"code":-32603,"message":""}: an internal error stating nothing,
            // which cannot be diagnosed from either end. The type is always
            // there, so say it, and put the whole exception somewhere readable.
            var why = string.IsNullOrWhiteSpace(ex.Message)
                ? $"{ex.GetType().Name} (no message)"
                : $"{ex.GetType().Name}: {ex.Message}";
            _opt.Audit?.Add("mcp.error", $"{method}: {why}", detail: ex.ToString());
            return Err(id, -32603, why);
        }
    }

    private IEnumerable<IMcpTool> AllTools()
    {
        var dynamic = _opt.DynamicTools?.Invoke() ?? Array.Empty<IMcpTool>();
        return dynamic.Count == 0 ? _opt.Tools : _opt.Tools.Concat(dynamic);
    }

    private object[] ListTools() =>
        AllTools().Select(t => (object)new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = t.InputSchema,
            annotations = new { readOnlyHint = t.ReadOnlyHint, openWorldHint = OpenWorld.Contains(t.Name) },
        }).ToArray();

    /// <summary>Tools whose results are other people's words — a screen, a file, the clipboard: the hub frames them as
    /// such (openWorldHint), so a page on this desk cannot give the agent orders (hub audit 2026-10-06, cl 6).</summary>
    public static readonly HashSet<string> OpenWorld = new(StringComparer.Ordinal)
    {
        "screen_capture", "screen_windows", "files_read", "files_list", "get_clipboard_text", "list_windows", "screenshot",
    };

    private async Task<object> CallToolAsync(JsonNode? parameters, string via)
    {
        var name = parameters?["name"]?.GetValue<string>()
            ?? throw new McpRpcException(-32602, "name required");
        var args = parameters?["arguments"];

        // The hidden tool: never in tools/list, so only the hub, which sealed it, calls it (§22.3).
        if (name == SealedSecrets.ToolName && _opt.Sealed is { } seal)
        {
            using var limit = new CancellationTokenSource(_opt.ToolCallTimeout);
            var used = await seal.FillAsync(args, limit.Token).ConfigureAwait(false);
            return new { content = used.Content(), isError = used.IsError };
        }

        // A value just typed, pasted or filled must not be read straight back (security review 2026-10-07).
        if (_opt.Sealed?.Blocks(name) is { } held)
        {
            _opt.Audit?.Add("mcp.held", $"{name}: {held}");
            return new { content = new[] { new { type = "text", text = held } }, isError = true };
        }

        var tool = AllTools().FirstOrDefault(t => t.Name == name)
            ?? throw new McpRpcException(-32601, $"Unknown tool: {name}");

        if (!_opt.Consent.IsEnabled(name))
        {
            _opt.Audit?.Add("mcp.denied", $"Tool {name} blocked by local consent");
            return new
            {
                content = new[] { new { type = "text", text = $"Tool '{name}' is disabled in DocaDesk settings." } },
                isError = true,
            };
        }

        _opt.Audit?.Add("mcp.call", $"tools/call {name}");
        using var deadline = new CancellationTokenSource(_opt.ToolCallTimeout);
        McpToolResult result;
        try
        {
            result = await tool.CallAsync(args, via, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            _opt.Audit?.Add("mcp.timeout", $"{name} after {_opt.ToolCallTimeout.TotalSeconds:0.#}s");
            result = new McpToolResult { IsError = true, Text = $"Error: '{name}' timed out after {_opt.ToolCallTimeout.TotalSeconds:0.#}s." };
        }

        return new
        {
            content = result.Content(),
            isError = result.IsError,
        };
    }

    private static string Err(JsonNode? id, int code, string message)
    {
        var o = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is null ? null : JsonNode.Parse(id.ToJsonString()),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        return o.ToJsonString();
    }
}

file sealed class McpRpcException : Exception
{
    public int Code { get; }
    public McpRpcException(int code, string message) : base(message) => Code = code;
}
