using DocaDesk.Core;

namespace DocaDesk.Tests;

/// <summary>The prompt events the hub really sends (hub audit 2026-10-06, cl 23).</summary>
public class PromptEventsTests
{
    [Theory]
    [InlineData("prompt.outcome", true)]
    [InlineData("prompt.progress", true)]
    [InlineData("prompt.updated", true)]
    [InlineData("prompt.new", false)]
    [InlineData("alert", false)]
    [InlineData(null, false)]
    public void RefreshesOnTheHubsOwnEvents(string? type, bool refresh) => Assert.Equal(refresh, PromptEvents.IsRefresh(type));

    [Fact]
    public void ClosedIsClosed() => Assert.True(PromptEvents.IsClosed("prompt.closed"));
}
