namespace DocaDesk.Capture;

/// <summary>Capture APIs — implemented in M5. Selection logic is interface-backed for unit tests.</summary>
public interface ICaptureBackend
{
    bool GraphicsCaptureSupported { get; }
    bool CanCaptureWindow(string windowId);
    bool PreferPrintWindowFallback(string windowId);
}

public sealed class CaptureBackendSelector
{
    private readonly ICaptureBackend _backend;

    public CaptureBackendSelector(ICaptureBackend backend) => _backend = backend;

    public enum Path { GraphicsCapture, PrintWindow, Unavailable }

    public Path Select(string windowId)
    {
        if (_backend.GraphicsCaptureSupported && _backend.CanCaptureWindow(windowId))
            return Path.GraphicsCapture;
        if (_backend.PreferPrintWindowFallback(windowId))
            return Path.PrintWindow;
        return Path.Unavailable;
    }
}
