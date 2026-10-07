using DocaDesk.Core;
using DocaDesk.Core.Models;
using Xunit;

namespace DocaDesk.Tests;

/// <summary>CapsWatcher: a monitor change while connected is reported once, and nothing is sent while nothing changed.</summary>
public class CapsWatcherTests
{
    private static DeviceCaps Caps(int w, int h, double dpr = 1) => new()
    {
        FormFactor = "desktop",
        Screen = new ScreenCaps { W = w, H = h, Dpr = dpr, Shape = "rect", Color = true },
        Input = new InputCaps { Touch = false, Text = true },
    };

    [Fact]
    public async Task Reports_only_when_the_screen_changes()
    {
        var now = Caps(1920, 1080);
        var sent = new List<DeviceCaps>();
        var w = new CapsWatcher(() => now, (c, _) => { sent.Add(c); return Task.CompletedTask; });
        w.Sent(now);
        Assert.False(await w.CheckAsync());
        now = Caps(3840, 2160, 1.5);   // a 4K monitor became the main display
        Assert.True(await w.CheckAsync());
        Assert.False(await w.CheckAsync());
        Assert.Single(sent);
        Assert.Equal(3840, sent[0].Screen!.W);
    }

    [Fact]
    public async Task A_failed_report_is_tried_again_at_the_next_check()
    {
        var calls = 0;
        var w = new CapsWatcher(() => Caps(1280, 800), (_, _) => ++calls == 1 ? throw new HttpRequestException("offline") : Task.CompletedTask);
        w.Sent(Caps(1920, 1080));
        await Assert.ThrowsAsync<HttpRequestException>(() => w.CheckAsync());
        Assert.True(await w.CheckAsync());
        Assert.Equal(2, calls);
    }
}
