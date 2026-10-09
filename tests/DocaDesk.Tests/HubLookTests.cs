using System.Text.Json;
using DocaDesk.Core;
using DocaDesk.Core.Look;
using DocaDesk.Core.Models;

namespace DocaDesk.Tests;

/// <summary>
/// The panel's look (PROTOCOL §14.1, hub device-look) read and mapped onto WinUI's theme resources, on the hub's own
/// fixtures (hub-fixtures\, copied from doca docs/api/fixtures; the copy is compared with the sibling checkout's when
/// one is reachable and has them).
/// </summary>
public class HubLookTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hub-fixtures", name));

    [Fact]
    public void Reads_the_hubs_fixture()
    {
        var look = PanelLookWire.Parse(Fixture("settings-look.json"));
        Assert.NotNull(look);
        Assert.Equal("pointsDaylight", look!.Theme);
        Assert.Equal("points", look.Skin);
        Assert.Equal("light", look.Ground);
        Assert.Equal("#17807a", look.Palette!["accent"]);
        Assert.Equal("IBM Plex Sans", look.Fonts!.Ui![0]);
        Assert.Equal(6, look.Radius!.Control);
        Assert.Equal(10, look.Radius.Card);
        Assert.Equal("92cd6d89b2ab", look.Etag);
    }

    [Fact]
    public void Points_daylight_is_a_light_theme_in_the_panels_colours()
    {
        var desk = DeskLook.From(PanelLookWire.Parse(Fixture("settings-look.json")), _ => false)!;
        Assert.False(desk.Dark);
        Assert.Equal(new LookColor(255, 0xf4, 0xf5, 0xf6), desk.Brushes["ApplicationPageBackgroundThemeBrush"]);
        Assert.Equal(new LookColor(255, 0xff, 0xff, 0xff), desk.Brushes["CardBackgroundFillColorDefaultBrush"]);
        Assert.Equal(new LookColor(255, 0xe3, 0xe7, 0xeb), desk.Brushes["CardStrokeColorDefaultBrush"]);
        Assert.Equal(new LookColor(255, 0x2a, 0x31, 0x38), desk.Brushes["TextFillColorPrimaryBrush"]);
        Assert.Equal(new LookColor(255, 0x5b, 0x65, 0x70), desk.Brushes["TextFillColorSecondaryBrush"]);
        // The accent reaches the controls' own keys, which WinUI resolves once and so are named one by one.
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentButtonBackground", "ToggleSwitchFillOn", "SelectorBarItemPillFill" })
            Assert.Equal(new LookColor(255, 0x17, 0x80, 0x7a), desk.Brushes[key]);
        Assert.Equal(new LookColor(255, 0xff, 0xff, 0xff), desk.Brushes["AccentButtonForeground"]);
        Assert.Equal(new LookColor(255, 0xc2, 0x3d, 0x3d), desk.Brushes["SystemFillColorCriticalBrush"]);
        Assert.Equal(6, desk.ControlRadius);
        Assert.Equal(10, desk.CardRadius);
        Assert.Equal(new FontChoice("IBM Plex Sans", true), desk.UiFont);
        Assert.Equal(new FontChoice("IBM Plex Mono", true), desk.MonoFont);
        Assert.Equal("92cd6d89b2ab", desk.Etag);
    }

    [Fact]
    public void Points_is_dark()
    {
        var desk = DeskLook.From(PanelLookWire.Parse(Fixture("settings-look-points-dark.json")), _ => false)!;
        Assert.True(desk.Dark);
        Assert.Equal(new LookColor(255, 0x05, 0x05, 0x07), desk.Brushes["ApplicationPageBackgroundThemeBrush"]);
        Assert.Equal(new LookColor(255, 0x57, 0xc9, 0xc2), desk.Brushes["ToggleSwitchFillOn"]);
        Assert.Equal(new LookColor(255, 0x05, 0x05, 0x07), desk.Brushes["ToggleSwitchKnobFillOn"]);   // onAccent
    }

    [Fact]
    public void The_ground_decides_light_or_dark_when_the_hub_does_not_say()
    {
        var dark = new PanelLook { Palette = new() { ["bg"] = "#101010", ["text"] = "#eeeeee", ["accent"] = "#3399ff" } };
        var light = new PanelLook { Palette = new() { ["bg"] = "#fafafa", ["text"] = "#111", ["accent"] = "#3399ff" } };
        Assert.True(DeskLook.From(dark)!.Dark);
        Assert.False(DeskLook.From(light)!.Dark);
        Assert.Equal(new LookColor(255, 0x11, 0x11, 0x11), DeskLook.From(light)!.Brushes["TextFillColorPrimaryBrush"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"look":{"palette":{"bg":"#000","text":"#fff"}}}""")]          // no accent
    [InlineData("""{"look":{"palette":{"bg":"var(--x)","text":"#fff","accent":"#0f0"}}}""")]   // not a colour
    [InlineData("""{"look":{}}""")]
    public void A_palette_without_ground_text_or_accent_is_no_look(string? json) =>
        Assert.Null(DeskLook.From(PanelLookWire.Parse(json ?? "")));

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    public void A_body_that_is_not_a_look_is_none(string json) => Assert.Null(PanelLookWire.Parse(json));

    [Theory]
    [InlineData("#abc", 255, 0xaa, 0xbb, 0xcc)]
    [InlineData("#17807A", 255, 0x17, 0x80, 0x7a)]
    [InlineData("#11223380", 0x80, 0x11, 0x22, 0x33)]
    public void Colours_are_read_in_css_order(string hex, int a, int r, int g, int b) =>
        Assert.Equal(new LookColor((byte)a, (byte)r, (byte)g, (byte)b), LookColor.Parse(hex));

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    [InlineData("rgb(1,2,3)")]
    public void Anything_else_is_not_a_colour(string v) => Assert.Null(LookColor.Parse(v));

    [Fact]
    public void A_font_the_app_does_not_carry_gives_way_to_the_next_as_in_a_browser()
    {
        // Modern's Inter is neither carried nor installed here: the next family Windows has.
        Assert.Equal(new FontChoice("Segoe UI", false),
            DeskLook.PickFont(["Inter", "Segoe UI", "sans-serif"], f => f == "Segoe UI", mono: false));
        Assert.Equal(new FontChoice("Segoe UI", false), DeskLook.PickFont(["Inter", "system-ui"], _ => false, mono: false));
        Assert.Equal(new FontChoice("Cascadia Code", false),
            DeskLook.PickFont(["JetBrains Mono", "Cascadia Code", "monospace"], f => f == "Cascadia Code", mono: true));
        Assert.Equal(new FontChoice("Consolas", false), DeskLook.PickFont(["Nope"], _ => false, mono: true));
        Assert.Null(DeskLook.PickFont(["Nope"], _ => false, mono: false));
        Assert.Equal(new FontChoice("IBM Plex Sans", true), DeskLook.PickFont(["'ibm plex sans'"], _ => false, mono: false));
    }

    [Fact]
    public void Corners_out_of_reason_keep_DocaDesks_own()
    {
        var look = new PanelLook
        {
            Palette = new() { ["bg"] = "#000000", ["text"] = "#ffffff", ["accent"] = "#00ff00" },
            Radius = new LookRadius { Control = 0, Card = 900 },
        };
        var desk = DeskLook.From(look)!;
        Assert.Equal(0, desk.ControlRadius);   // Classic is square
        Assert.Equal(8, desk.CardRadius);
    }

    [Fact]
    public void Settings_changed_with_look_true_asks_for_the_look_again()
    {
        var ev = DocaJson.Deserialize<EventEnvelope>(Fixture("settings.changed.json"));
        Assert.True(PanelLookWire.IsLookChange(ev));

        var notLook = DocaJson.Deserialize<EventEnvelope>("""{"type":"settings.changed","payload":{"keys":["sidebar"],"look":false}}""");
        Assert.False(PanelLookWire.IsLookChange(notLook));
        var other = DocaJson.Deserialize<EventEnvelope>("""{"type":"profile.changed","payload":{"look":true}}""");
        Assert.False(PanelLookWire.IsLookChange(other));
        Assert.False(PanelLookWire.IsLookChange(null));
    }

    [Fact]
    public void The_copied_fixtures_are_the_hubs()
    {
        if (DocaRepo.ClientJs is null) return;   // no sibling checkout reachable
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(DocaRepo.ClientJs)!, "..", ".."));
        foreach (var name in new[] { "settings-look.json", "settings.changed.json" })
        {
            var hub = Path.Combine(root, "docs", "api", "fixtures", name);
            if (!File.Exists(hub)) continue;   // a checkout older than device-look
            Assert.Equal(Normal(File.ReadAllText(hub)), Normal(Fixture(name)));
        }
    }

    private static string Normal(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);
}
