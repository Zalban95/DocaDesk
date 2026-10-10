using System.Text.Json;
using DocaDesk.Core;

namespace DocaDesk.Tests;

/// <summary>
/// Meetings on the desk (hub PROTOCOL §23.4), on the hub's own frames (hub-fixtures\, copied from doca
/// docs/api/fixtures): a ring is a call to join, the controlled-machine banner names the controller, and the panel's
/// WebView is given the camera, the microphone and a screen capture on the hub's own address only.
/// </summary>
public class MeetingsTests
{
    private static readonly Uri Hub = new("https://hub.tail.ts.net:4242/");

    private static JsonElement Payload(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hub-fixtures", name))).RootElement.GetProperty("payload").Clone();

    [Fact]
    public void A_ring_names_the_meeting_to_join()
    {
        var ring = Meetings.RingOf(Payload("alert-meeting.json"));
        Assert.NotNull(ring);
        Assert.Equal("m0123456789ab", ring!.Id);
        Assert.Equal("Alice", ring.By);
        Assert.Equal("Design sync", ring.Title);
    }

    [Fact]
    public void A_plain_alert_is_no_call_and_an_older_hubs_ring_is_read_from_its_link()
    {
        Assert.Null(Meetings.RingOf(Payload("alert.json"), "turbine-front.png is in the chat."));
        var old = JsonDocument.Parse("""{"title":"Bob is calling"}""").RootElement;
        Assert.Equal("m00000000beef", Meetings.RingOf(old, "Sync\nJoin: https://hub/meet/m00000000beef")!.Id);
        Assert.Null(Meetings.RingOf(old, "Join: https://hub/meet/../../x"));
    }

    [Fact]
    public void The_banner_says_who_controls_this_computer()
    {
        var c = Meetings.ControlOf(Payload("meeting.control.json"));
        Assert.NotNull(c);
        Assert.True(c!.Active);
        Assert.Equal("Bob is controlling this computer", Meetings.BannerText(c));
        Assert.Null(Meetings.ControlOf(JsonDocument.Parse("""{"grant":"g","state":"maybe"}""").RootElement));
        Assert.False(Meetings.ControlOf(JsonDocument.Parse("""{"grant":"g","state":"ended","why":"x"}""").RootElement)!.Active);
    }

    [Theory]
    [InlineData("Camera", "https://hub.tail.ts.net:4242/", PermissionAnswer.Allow)]
    [InlineData("Microphone", "https://hub.tail.ts.net:4242/meet/m0123456789ab", PermissionAnswer.Allow)]
    [InlineData("Camera", "https://example.com/", PermissionAnswer.Deny)]                      // another site
    [InlineData("Microphone", "https://hub.tail.ts.net:9999/", PermissionAnswer.Deny)]         // another port
    [InlineData("Camera", "http://hub.tail.ts.net:4242/", PermissionAnswer.Deny)]              // another scheme
    [InlineData("Camera", "https://user@hub.tail.ts.net:4242/", PermissionAnswer.Deny)]        // user info
    [InlineData("Geolocation", "https://hub.tail.ts.net:4242/", PermissionAnswer.Default)]     // not a meeting's: WebView2 decides
    public void The_camera_and_microphone_go_to_the_hubs_own_page_only(string kind, string origin, PermissionAnswer want) =>
        Assert.Equal(want, Meetings.Permission(kind, new Uri(origin), Hub));

    [Fact]
    public void A_screen_capture_only_for_the_hub_and_rooms_only_on_the_hub()
    {
        Assert.True(Meetings.ScreenCapture(new Uri("https://hub.tail.ts.net:4242/?meet=m0123456789ab"), Hub));
        Assert.False(Meetings.ScreenCapture(new Uri("https://evil.example/"), Hub));
        Assert.False(Meetings.ScreenCapture(null, Hub));
        Assert.Equal("https://hub.tail.ts.net:4242/meet/m0123456789ab", Meetings.RoomUri(new Uri("https://hub.tail.ts.net:4242/?x=1"), "m0123456789ab")!.ToString());
        Assert.Null(Meetings.RoomUri(Hub, "../admin"));
    }

    [Fact]
    public void Stop_on_the_banner_holds_the_input_tools_until_released()
    {
        DocaDesk.Mcp.MeetingHold.Hold(TimeSpan.FromSeconds(5));
        Assert.True(DocaDesk.Mcp.MeetingHold.Held);
        DocaDesk.Mcp.MeetingHold.Release();
        Assert.False(DocaDesk.Mcp.MeetingHold.Held);
    }
}
