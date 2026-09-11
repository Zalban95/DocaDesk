using System.Text.Json.Nodes;
using DocaDesk.Capture;
using DocaDesk.Core.Audit;
using DocaDesk.Core.Net;
using DocaDesk.Mcp;
using Windows.ApplicationModel.DataTransfer;

namespace DocaDesk.Services;

public static class DeskTools
{
    public static IReadOnlyList<IMcpTool> Create(
        ScreenCapturer capturer,
        Func<DocaClient?> clientFactory,
        AuditLog audit,
        Action? onCaptureFlash)
    {
        return
        [
            new ListWindowsTool(),
            new ScreenshotTool(capturer, clientFactory, audit, onCaptureFlash),
            new GetClipboardTool(audit),
            new SetClipboardTool(audit),
            new OpenUrlTool(audit),
        ];
    }
}

file sealed class ListWindowsTool : IMcpTool
{
    public string Name => "list_windows";
    public string Description => "List visible windows: id, title, process, minimized.";
    public bool ReadOnlyHint => true;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        var windows = WindowEnumerator.List();
        var lines = windows.Select(w =>
            $"{w.Id}\tmon={w.MonitorIndex}\t{w.ProcessName}\t{(w.Minimized ? "min" : "norm")}\t{w.Title}");
        return Task.FromResult(new McpToolResult { Text = string.Join('\n', lines) });
    }
}

file sealed class ScreenshotTool : IMcpTool
{
    private readonly ScreenCapturer _cap;
    private readonly Func<DocaClient?> _client;
    private readonly AuditLog _audit;
    private readonly Action? _flash;

    public ScreenshotTool(ScreenCapturer cap, Func<DocaClient?> client, AuditLog audit, Action? flash)
    {
        _cap = cap;
        _client = client;
        _audit = audit;
        _flash = flash;
    }

    public string Name => "screenshot";
    public string Description => "Capture a window or monitor; uploads to Doca media and returns media id/url as text.";
    public bool ReadOnlyHint => true;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["windowId"] = new JsonObject { ["type"] = "string", ["description"] = "Window id from list_windows" },
            ["monitor"] = new JsonObject { ["type"] = "integer", ["description"] = "Monitor index (default 0)" },
        },
    };

    public async Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        var client = _client();
        if (client is null)
            return new McpToolResult { IsError = true, Text = "Not paired — cannot upload media." };

        CaptureResult shot;
        string target;
        try
        {
            var windowId = args?["windowId"]?.GetValue<string>();
            // Pull live mediaBytes from capabilities when paired
            try
            {
                var caps = await client.GetCapabilitiesAsync(ct).ConfigureAwait(false);
                if (caps.Limits is { } lim &&
                    lim.TryGetProperty("mediaBytes", out var mb) &&
                    mb.TryGetInt64(out var budget))
                {
                    _cap.MediaBytesBudget = budget;
                }
            }
            catch { /* keep default budget */ }

            if (!string.IsNullOrWhiteSpace(windowId))
            {
                target = $"window:{windowId}";
                shot = _cap.CaptureWindow(windowId!);
            }
            else
            {
                var monitor = args?["monitor"]?.GetValue<int>() ?? 0;
                target = $"monitor:{monitor}";
                shot = _cap.CaptureMonitor(monitor);
            }
        }
        catch (Exception ex)
        {
            _audit.Add("capture.fail", ex.Message, sessionId);
            return new McpToolResult { IsError = true, Text = ex.Message };
        }

        _flash?.Invoke();
        _audit.Add("capture", $"Screenshot {target} via {shot.PathUsed} ({shot.Width}x{shot.Height} {shot.Mime})", sessionId, shot.EncodingReason);

        await using var stream = new MemoryStream(shot.Bytes);
        var uploaded = await client.UploadMediaAsync(
            stream,
            shot.Mime.Contains("png") ? "capture.png" : "capture.jpg",
            shot.Mime,
            new { w = shot.Width, h = shot.Height, source = "docadesk", ext = new { path = shot.PathUsed } },
            ct: ct).ConfigureAwait(false);

        var media = uploaded.Media;
        var text =
            $"mediaId={media?.Id}\nurl={media?.Url}\nbytes={media?.Bytes}\nmime={media?.Mime}\nencoding={shot.EncodingReason}\npath={shot.PathUsed}";
        return new McpToolResult { Text = text };
    }
}

file sealed class GetClipboardTool : IMcpTool
{
    private readonly AuditLog _audit;
    public GetClipboardTool(AuditLog audit) => _audit = audit;
    public string Name => "get_clipboard_text";
    public string Description => "Read the Windows clipboard as text only.";
    public bool ReadOnlyHint => true;
    public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public async Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        _audit.Add("clipboard.read", "get_clipboard_text", sessionId);
        var dp = Clipboard.GetContent();
        if (!dp.Contains(StandardDataFormats.Text))
            return new McpToolResult { Text = "" };
        var text = await dp.GetTextAsync();
        return new McpToolResult { Text = text };
    }
}

file sealed class SetClipboardTool : IMcpTool
{
    private readonly AuditLog _audit;
    public SetClipboardTool(AuditLog audit) => _audit = audit;
    public string Name => "set_clipboard_text";
    public string Description => "Set the Windows clipboard text.";
    public bool ReadOnlyHint => false;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["text"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("text"),
    };

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        var text = args?["text"]?.GetValue<string>() ?? "";
        _audit.Add("clipboard.write", "set_clipboard_text", sessionId, detail: $"len={text.Length}");
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        return Task.FromResult(new McpToolResult { Text = "ok" });
    }
}

file sealed class OpenUrlTool : IMcpTool
{
    private readonly AuditLog _audit;
    public OpenUrlTool(AuditLog audit) => _audit = audit;
    public string Name => "open_url";
    public string Description => "Open an http(s) URL in the user's default browser.";
    public bool ReadOnlyHint => false;
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["url"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("url"),
    };

    public async Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct)
    {
        var url = args?["url"]?.GetValue<string>() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new McpToolResult { IsError = true, Text = "Only http/https URLs are allowed." };
        }

        _audit.Add("open_url", uri.Host, sessionId);
        await Windows.System.Launcher.LaunchUriAsync(uri);
        return new McpToolResult { Text = "ok" };
    }
}
