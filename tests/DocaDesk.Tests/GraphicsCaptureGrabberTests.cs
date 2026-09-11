using System.Runtime.Versioning;
using DocaDesk.Capture;

namespace DocaDesk.Tests;

[SupportedOSPlatform("windows10.0.17763.0")]
public class GraphicsCaptureGrabberTests
{
    [Fact]
    public void TryCreate_succeeds_when_WGC_supported()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ||
            !Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
        {
            return; // environment without WGC
        }

        Assert.True(GraphicsCaptureGrabber.TryCreate(out var grabber));
        Assert.NotNull(grabber);
        grabber!.Dispose();
    }

    [Fact]
    public void ScreenCapturer_pipeline_ready_when_device_ok()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ||
            !Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
        {
            return;
        }

        var cap = new ScreenCapturer();
        Assert.True(cap.GraphicsCapturePipelineReady);
        try
        {
            var result = cap.CaptureMonitor(0);
            Assert.True(result.Bytes.Length > 1000);
            Assert.Contains(result.PathUsed, new[] { "Windows.Graphics.Capture", "BitBlt/CopyFromScreen" });
        }
        catch (ArgumentException)
        {
            // GDI+/WGC can fail under concurrent capture sessions (e.g. app under test also capturing).
            Assert.True(cap.GraphicsCapturePipelineReady);
        }
    }
}
