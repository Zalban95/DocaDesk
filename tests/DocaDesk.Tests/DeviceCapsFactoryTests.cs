using DocaDesk.Core;
using DocaDesk.Core.Models;

namespace DocaDesk.Tests;

public class DeviceCapsFactoryTests
{
    [Fact]
    public void Desktop_caps_are_honest_shape()
    {
        var caps = DeviceCapsFactory.FromMachine();
        Assert.Equal("desktop", caps.FormFactor);
        Assert.Equal("rect", caps.Screen!.Shape);
        Assert.True(caps.Screen.Color);
        Assert.True(caps.Input!.Text);
        Assert.False(caps.Input.Voice);
        Assert.False(caps.Input.Camera);
        Assert.Contains("svg", caps.Render!);
        Assert.Empty(caps.Exec!);
        Assert.DoesNotContain("heartRate", caps.Sensors ?? []);
        Assert.True((caps.Sensors ?? []).All(s => s is "battery"));
    }
}
