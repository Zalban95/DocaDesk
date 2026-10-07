using DocaDesk.Mcp;

namespace DocaDesk.Tests;

/// <summary>What the desk reads of other people's words is marked so (hub audit 2026-10-06, cl 6).</summary>
public class OpenWorldTests
{
    [Theory]
    [InlineData("screen_capture", true)]
    [InlineData("files_read", true)]
    [InlineData("get_clipboard_text", true)]
    [InlineData("shell", false)]
    [InlineData("files_write", false)]
    public void ReadingToolsAreOpenWorld(string name, bool open) => Assert.Equal(open, McpHttpListener.OpenWorld.Contains(name));
}
