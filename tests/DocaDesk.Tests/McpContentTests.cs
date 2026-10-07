using System.Text.Json;
using DocaDesk.Mcp;

namespace DocaDesk.Tests;

/// <summary>A screenshot reaches the hub as an MCP image part (hub audit 2026-10-06, cl 3).</summary>
public class McpContentTests
{
    [Fact]
    public void AnImageIsSentBesideTheText()
    {
        var r = new McpToolResult { Text = "mediaId=med_1", ImageBytes = new byte[] { 1, 2, 3 }, ImageMime = "image/png" };
        var json = JsonSerializer.Serialize(r.Content());
        Assert.Contains("\"type\":\"image\"", json);
        Assert.Contains("\"data\":\"AQID\"", json);
        Assert.Contains("\"mimeType\":\"image/png\"", json);
    }

    [Fact]
    public void TextAloneIsOnePart() => Assert.Single(new McpToolResult { Text = "ok" }.Content());
}
