using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DocaDesk.Capture;
using DocaDesk.Core.Audit;
using DocaDesk.Core.Net;
using DocaDesk.Mcp;

namespace DocaDesk.Tests;

public class McpToolsIntegrationTests
{
    [Fact]
    public async Task List_windows_and_screenshot_via_jsonrpc()
    {
        var url = Environment.GetEnvironmentVariable("DOCADESK_E2E_URL");
        var token = Environment.GetEnvironmentVariable("DOCADESK_E2E_TOKEN");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
        {
            // Fall back to files written by local acceptance
            var tokenPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".agent", "e2e-token.json");
            tokenPath = Path.GetFullPath(tokenPath);
            if (!File.Exists(tokenPath))
                return;
            using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(tokenPath));
            token = doc.RootElement.GetProperty("token").GetString();
            url = Environment.GetEnvironmentVariable("DOCADESK_E2E_URL")
                  ?? "https://portal.tail08f157.ts.net:9442/";
        }

        await using var client = new DocaClient(new DocaClientOptions
        {
            BaseAddress = new Uri(url!),
            Token = token,
            CertificatePolicy = new Core.Security.CertificatePolicy(
                Environment.GetEnvironmentVariable("DOCADESK_E2E_PIN")),
        });

        try
        {
            _ = await client.GetCapabilitiesAsync();
        }
        catch
        {
            return; // server not up
        }

        var consent = new ToolConsent();
        consent.Set("list_windows", true);
        consent.Set("screenshot", true);
        var audit = new AuditLog(Path.Combine(Path.GetTempPath(), "docadesk-audit-test.jsonl"));
        var capturer = new ScreenCapturer();

        var tools = new IMcpTool[]
        {
            new TestListWindowsTool(),
            new TestScreenshotTool(capturer, client, audit),
        };

        var port = FreePort();
        var secret = McpHttpListener.NewPathSecret();
        await using var listener = new McpHttpListener(new McpListenerOptions
        {
            PathSecret = secret,
            Consent = consent,
            Audit = audit,
            Tools = tools,
        });
        await listener.StartLoopbackForTestsAsync(port);
        var mcpUrl = listener.BoundUrl!;

        var list = await Rpc(mcpUrl, "tools/call", """{"name":"list_windows","arguments":{}}""");
        Assert.DoesNotContain("isError\":true", list.Replace(" ", ""));
        Assert.True(list.Contains("norm", StringComparison.Ordinal) || list.Contains("min", StringComparison.Ordinal),
            "expected at least one window row");

        var shot = await Rpc(mcpUrl, "tools/call", """{"name":"screenshot","arguments":{"monitor":0}}""");
        Assert.Contains("mediaId=", shot);
        Assert.Contains("med_", shot);
    }

    private sealed class TestListWindowsTool : IMcpTool
    {
        public string Name => "list_windows";
        public string Description => "list";
        public bool ReadOnlyHint => true;
        public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
        {
            var windows = WindowEnumerator.List();
            var lines = windows.Select(w =>
                $"{w.Id}\tmon={w.MonitorIndex}\t{w.ProcessName}\t{(w.Minimized ? "min" : "norm")}\t{w.Title}");
            return Task.FromResult(new McpToolResult { Text = string.Join('\n', lines) });
        }
    }

    private sealed class TestScreenshotTool : IMcpTool
    {
        private readonly ScreenCapturer _cap;
        private readonly DocaClient _client;
        private readonly AuditLog _audit;
        public TestScreenshotTool(ScreenCapturer cap, DocaClient client, AuditLog audit)
        {
            _cap = cap; _client = client; _audit = audit;
        }
        public string Name => "screenshot";
        public string Description => "shot";
        public bool ReadOnlyHint => true;
        public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };
        public async Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
        {
            var shot = _cap.CaptureMonitor(0);
            _audit.Add("capture", "test screenshot", sessionId);
            await using var stream = new MemoryStream(shot.Bytes);
            var uploaded = await _client.UploadMediaAsync(stream, "t.png", shot.Mime,
                new { w = shot.Width, h = shot.Height, source = "test" }, ct: ct);
            return new McpToolResult
            {
                Text = $"mediaId={uploaded.Media?.Id}\nurl={uploaded.Media?.Url}\npath={shot.PathUsed}",
            };
        }
    }

    private static async Task<string> Rpc(string url, string method, string paramsJson)
    {
        var body = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{paramsJson}}}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res = await http.PostAsync(url, content);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return text;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
