using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocaDesk.Core.Audit;

namespace DocaDesk.Mcp;

public interface IMcpTool
{
    string Name { get; }
    string Description { get; }
    bool ReadOnlyHint { get; }
    JsonObject InputSchema { get; }
    Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct);
}

public sealed class McpToolResult
{
    public bool IsError { get; init; }
    public string Text { get; init; } = "";
}

public sealed class ToolConsent
{
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.Ordinal)
    {
        ["list_windows"] = false,
        ["screenshot"] = false,
        ["get_clipboard_text"] = false,
        ["set_clipboard_text"] = false,
        ["open_url"] = false,
    };

    public bool IsEnabled(string name) => _enabled.TryGetValue(name, out var v) && v;
    public void Set(string name, bool on) { if (_enabled.ContainsKey(name)) _enabled[name] = on; }
    public IReadOnlyDictionary<string, bool> Snapshot() => new Dictionary<string, bool>(_enabled);
}

public sealed class McpListenerOptions
{
    public required string PathSecret { get; set; }
    public string? BindAddress { get; set; }
    public int Port { get; set; } = 8742;
    public string? AllowedRemoteHost { get; set; }
    public bool AllowLoopback { get; set; }
    public ToolConsent Consent { get; set; } = new();
    public AuditLog? Audit { get; set; }
    public IReadOnlyList<IMcpTool> Tools { get; set; } = Array.Empty<IMcpTool>();
}

/// <summary>
/// Hand-rolled HTTP MCP server matching Doca's client: POST JSON-RPC → JSON.
/// Tailscale bind only; secret path; remote-address check.
/// </summary>
public sealed class McpHttpListener : IAsyncDisposable
{
    private readonly McpListenerOptions _opt;
    private SimpleHttpServer? _server;

    public bool IsRunning => _server?.IsRunning == true;
    public string? BoundUrl { get; private set; }
    public string? LastError { get; private set; }

    public McpHttpListener(McpListenerOptions options) => _opt = options;

    public static string NewPathSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string? FindTailscaleIpv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            var name = nic.Name + " " + nic.Description;
            var looksTs = name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address.ToString();
                if (ip.StartsWith("100.", StringComparison.Ordinal) || looksTs)
                    return ip;
            }
        }
        return null;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        LastError = null;

        var bind = _opt.BindAddress ?? FindTailscaleIpv4();
        if (string.IsNullOrEmpty(bind))
        {
            LastError = "No Tailscale IPv4 found. Connect Tailscale before starting the MCP listener.";
            throw new InvalidOperationException(LastError);
        }

        if (bind is "0.0.0.0" or "::")
            throw new InvalidOperationException("Refusing to bind MCP to a wildcard address.");

        if (!IPAddress.TryParse(bind, out var ip))
            throw new InvalidOperationException("Invalid bind address.");

        _server = new SimpleHttpServer(ip, _opt.Port, HandleAsync);
        _server.Start();
        BoundUrl = $"http://{bind}:{_opt.Port}/mcp/{_opt.PathSecret}";
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Test helper: bind loopback (not for production).</summary>
    public async Task StartLoopbackForTestsAsync(int port, CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        _opt.BindAddress = "127.0.0.1";
        _opt.Port = port;
        _opt.AllowLoopback = true;
        _server = new SimpleHttpServer(IPAddress.Loopback, port, HandleAsync);
        _server.Start();
        BoundUrl = $"http://127.0.0.1:{port}/mcp/{_opt.PathSecret}";
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
        BoundUrl = null;
    }

    private async Task<HttpResponse> HandleAsync(HttpRequest req)
    {
        if (!IsRemoteAllowed(req.Remote))
            return new HttpResponse(403, "text/plain", "forbidden remote");

        var expected = "/mcp/" + _opt.PathSecret;
        if (!string.Equals(req.Path, expected, StringComparison.Ordinal))
            return new HttpResponse(404, "text/plain", "not found");

        if (!string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return new HttpResponse(405, "text/plain", "POST only");

        var json = await DispatchJsonRpcAsync(req.Body).ConfigureAwait(false);
        return new HttpResponse(200, "application/json", json);
    }

    private bool IsRemoteAllowed(IPAddress? remote)
    {
        if (remote is null) return false;
        if (IPAddress.IsLoopback(remote))
            return _opt.AllowLoopback;

        if (!string.IsNullOrEmpty(_opt.AllowedRemoteHost))
        {
            try
            {
                var addrs = Dns.GetHostAddresses(_opt.AllowedRemoteHost);
                if (addrs.Any(a => a.Equals(remote)))
                    return true;
            }
            catch { /* ignore */ }
        }

        return remote.ToString().StartsWith("100.", StringComparison.Ordinal);
    }

    private async Task<string> DispatchJsonRpcAsync(string body)
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
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "DocaDesk", version = "0.1.0" },
                },
                "tools/list" => new { tools = ListTools() },
                "tools/call" => await CallToolAsync(parameters).ConfigureAwait(false),
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
            return Err(id, -32603, ex.Message);
        }
    }

    private object[] ListTools() =>
        _opt.Tools.Select(t => (object)new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = t.InputSchema,
            annotations = new { readOnlyHint = t.ReadOnlyHint },
        }).ToArray();

    private async Task<object> CallToolAsync(JsonNode? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>()
            ?? throw new McpRpcException(-32602, "name required");
        var args = parameters?["arguments"];
        var tool = _opt.Tools.FirstOrDefault(t => t.Name == name)
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
        var result = await tool.CallAsync(args, "http", CancellationToken.None).ConfigureAwait(false);
        return new
        {
            content = new[] { new { type = "text", text = result.Text } },
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

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

file sealed class McpRpcException : Exception
{
    public int Code { get; }
    public McpRpcException(int code, string message) : base(message) => Code = code;
}
