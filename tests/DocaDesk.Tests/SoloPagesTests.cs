using DocaDesk.Core;

namespace DocaDesk.Tests;

public class SoloPagesTests
{
    private static readonly Uri Hub = new("https://hub.tail.ts.net:4242/");

    [Theory]
    [InlineData("https://hub.tail.ts.net:4242/?view=workstream", "workstream")]
    [InlineData("https://hub.tail.ts.net:4242/d/dev_abc/?view=ambient", "ambient")]
    [InlineData("https://hub.tail.ts.net:4242/?x=1&view=live", "live")]
    public void The_hubs_own_page_alone_opens_in_a_DocaDesk_window(string url, string page) =>
        Assert.Equal(page, SoloPages.ViewOf(new Uri(url), Hub));

    [Theory]
    [InlineData("https://example.com/?view=workstream")]              // another site
    [InlineData("https://hub.tail.ts.net:9999/?view=workstream")]     // another port
    [InlineData("https://hub.tail.ts.net:4242/docs/?view=workstream")] // not the panel
    [InlineData("https://hub.tail.ts.net:4242/")]                     // the whole panel
    [InlineData("https://hub.tail.ts.net:4242/?view=../etc")]          // not a page name
    public void Anything_else_still_goes_to_the_browser(string url) =>
        Assert.Null(SoloPages.ViewOf(new Uri(url), Hub));

    [Fact]
    public void The_user_agent_says_DocaDesk_and_its_version() =>
        Assert.Equal($"DocaDesk/{DocaDeskConstants.ClientVersion}", SoloPages.UserAgentMark);
}
