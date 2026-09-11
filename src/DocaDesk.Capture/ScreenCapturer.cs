using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DocaDesk.Capture;

public sealed record WindowInfo(
    string Id,
    string Title,
    string ProcessName,
    int MonitorIndex,
    bool Minimized,
    IntPtr Handle);

public sealed record CaptureResult(
    byte[] Bytes,
    string Mime,
    int Width,
    int Height,
    string EncodingReason,
    string PathUsed);

[SupportedOSPlatform("windows")]
public static class WindowEnumerator
{
    public static IReadOnlyList<WindowInfo> List()
    {
        var list = new List<WindowInfo>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var len = GetWindowTextLength(hWnd);
            if (len <= 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            string proc = "";
            try
            {
                proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            }
            catch { /* access denied */ }

            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(hWnd, ref placement);
            var minimized = placement.showCmd == 2; // SW_SHOWMINIMIZED

            var monitor = MonitorFromWindow(hWnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var monitorIndex = IndexOfMonitor(monitor);

            list.Add(new WindowInfo(
                Id: hWnd.ToInt64().ToString("x"),
                Title: title,
                ProcessName: proc,
                MonitorIndex: monitorIndex,
                Minimized: minimized,
                Handle: hWnd));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static int IndexOfMonitor(IntPtr hMonitor)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            // Compare via device name is fragile; use Bounds center MonitorFromPoint
            var b = screens[i].Bounds;
            var center = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
            var h = MonitorFromPoint(new POINT { X = center.X, Y = center.Y }, 1);
            if (h == hMonitor) return i;
        }
        return 0;
    }

    public static IntPtr? FindHandle(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) return null;
        if (long.TryParse(windowId, System.Globalization.NumberStyles.HexNumber, null, out var hex))
            return new IntPtr(hex);
        if (long.TryParse(windowId, out var dec))
            return new IntPtr(dec);
        return null;
    }

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public Point ptMinPosition;
        public Point ptMaxPosition;
        public Rectangle rcNormalPosition;
    }
}

[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class ScreenCapturer
{
    private const int DefaultLongEdge = 1600;
    private GraphicsCaptureGrabber? _grabber;
    private bool _grabberInitTried;

    /// <summary>OS reports WGC available (API present). Prefer <see cref="GraphicsCapturePipelineReady"/> for actual capture.</summary>
    public bool GraphicsCaptureSupported
    {
        get
        {
            try
            {
                return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134)
                    && Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True when a free-threaded WGC + D3D device could be created.</summary>
    public bool GraphicsCapturePipelineReady => EnsureGrabber() is not null;

    private GraphicsCaptureGrabber? EnsureGrabber()
    {
        if (_grabberInitTried) return _grabber;
        _grabberInitTried = true;
        if (!GraphicsCaptureSupported) return null;
        if (GraphicsCaptureGrabber.TryCreate(out var g))
            _grabber = g;
        return _grabber;
    }

    public CaptureResult CaptureMonitor(int monitorIndex = 0, int longEdge = DefaultLongEdge)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (monitorIndex < 0 || monitorIndex >= screens.Length)
            throw new InvalidOperationException($"Monitor {monitorIndex} not found ({screens.Length} available).");
        var bounds = screens[monitorIndex].Bounds;

        var grabber = EnsureGrabber();
        if (grabber is not null)
        {
            try
            {
                var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                var hMon = MonitorFromPoint(new POINT { X = center.X, Y = center.Y }, 1);
                using var bmp = grabber.CaptureMonitor(hMon, TimeSpan.FromSeconds(5));
                return Encode(bmp, preferJpeg: true, longEdge, "Windows.Graphics.Capture", MediaBytesBudget);
            }
            catch
            {
                // Fall through to GDI.
            }
        }

        using var gdi = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(gdi))
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }
        return Encode(gdi, preferJpeg: true, longEdge, "BitBlt/CopyFromScreen", MediaBytesBudget);
    }

    /// <summary>From GET /capabilities limits.mediaBytes when known; Appendix A default otherwise.</summary>
    public long MediaBytesBudget { get; set; } = 1_572_864;

    public CaptureResult CaptureWindow(string windowId, int longEdge = DefaultLongEdge)
    {
        var hwnd = WindowEnumerator.FindHandle(windowId)
            ?? throw new InvalidOperationException($"Unknown window id '{windowId}'.");
        if (!IsWindow(hwnd))
            throw new InvalidOperationException("Window handle is no longer valid.");

        var selector = new CaptureBackendSelector(new RuntimeBackend(this, hwnd));
        var path = selector.Select(windowId);

        return path switch
        {
            CaptureBackendSelector.Path.GraphicsCapture => CaptureViaGraphicsCapture(hwnd, longEdge),
            CaptureBackendSelector.Path.PrintWindow => CaptureViaPrintWindow(hwnd, longEdge),
            _ => throw new InvalidOperationException(
                "Neither Windows.Graphics.Capture nor PrintWindow can capture this window (minimized/legacy/denied)."),
        };
    }

    private CaptureResult CaptureViaGraphicsCapture(IntPtr hwnd, int longEdge)
    {
        var grabber = EnsureGrabber()
            ?? throw new InvalidOperationException("Windows.Graphics.Capture device could not be created.");
        try
        {
            using var bmp = grabber.CaptureWindow(hwnd, TimeSpan.FromSeconds(5));
            if (IsMostlyBlack(bmp))
                throw new InvalidOperationException("Windows.Graphics.Capture returned a black frame.");
            return Encode(bmp, preferJpeg: false, longEdge, "Windows.Graphics.Capture", MediaBytesBudget);
        }
        catch (Exception wgcEx)
        {
            // Brief §5.4: PrintWindow is the fallback when WGC cannot handle the window.
            try
            {
                return CaptureViaPrintWindow(hwnd, longEdge);
            }
            catch (Exception pwEx)
            {
                throw new InvalidOperationException(
                    $"Capture failed. WGC: {wgcEx.Message}; PrintWindow: {pwEx.Message}");
            }
        }
    }

    private CaptureResult CaptureViaPrintWindow(IntPtr hwnd, int longEdge)
    {
        GetClientRect(hwnd, out var rect);
        var w = Math.Max(1, rect.Right - rect.Left);
        var h = Math.Max(1, rect.Bottom - rect.Top);
        if (w <= 1 || h <= 1)
        {
            // try outer rect
            GetWindowRect(hwnd, out var wr);
            w = Math.Max(1, wr.Right - wr.Left);
            h = Math.Max(1, wr.Bottom - wr.Top);
        }

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try
            {
                // PW_RENDERFULLCONTENT = 0x00000002 — helps some HW-accelerated windows
                var ok = PrintWindow(hwnd, hdc, 0x00000002);
                if (!ok)
                    ok = PrintWindow(hwnd, hdc, 0);
                if (!ok)
                    throw new InvalidOperationException("PrintWindow failed for this window.");
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        // Detect near-black frames (common PrintWindow failure mode)
        if (IsMostlyBlack(bmp))
            throw new InvalidOperationException(
                "Capture returned a black frame (PrintWindow). Windows.Graphics.Capture may be required for this window; neither path succeeded with usable pixels.");

        return Encode(bmp, preferJpeg: false, longEdge, "PrintWindow", MediaBytesBudget);
    }

    public static CaptureResult Encode(Bitmap source, bool preferJpeg, int longEdge, string pathUsed, long mediaBytesBudget = 1_572_864)
    {
        using var scaled = Downscale(source, longEdge);
        using var ms = new MemoryStream();
        string mime;
        string reason;
        var softLimit = Math.Max(100_000, mediaBytesBudget - 100_000);
        if (preferJpeg)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
            scaled.Save(ms, codec, ep);
            mime = "image/jpeg";
            reason = $"JPEG for photographic/monitor content (budget {mediaBytesBudget} bytes from capabilities)";
        }
        else
        {
            scaled.Save(ms, ImageFormat.Png);
            mime = "image/png";
            reason = "PNG for window/text legibility";
            if (ms.Length > softLimit)
            {
                ms.SetLength(0);
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
                scaled.Save(ms, codec, ep);
                mime = "image/jpeg";
                reason = $"JPEG fallback because PNG exceeded soft limit {softLimit} (mediaBytes {mediaBytesBudget})";
            }
        }

        return new CaptureResult(ms.ToArray(), mime, scaled.Width, scaled.Height, reason, pathUsed);
    }

    private static Bitmap Downscale(Bitmap source, int longEdge)
    {
        var max = Math.Max(source.Width, source.Height);
        if (max <= longEdge) return new Bitmap(source);
        var scale = longEdge / (double)max;
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, w, h);
        return bmp;
    }

    private static bool IsMostlyBlack(Bitmap bmp)
    {
        var samples = 0;
        var dark = 0;
        for (var y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 16))
        for (var x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 16))
        {
            var c = bmp.GetPixel(x, y);
            samples++;
            if (c.R < 8 && c.G < 8 && c.B < 8) dark++;
        }
        return samples > 0 && dark / (double)samples > 0.95;
    }

    private sealed class RuntimeBackend : ICaptureBackend
    {
        private readonly ScreenCapturer _owner;
        private readonly IntPtr _hwnd;
        public RuntimeBackend(ScreenCapturer owner, IntPtr hwnd) { _owner = owner; _hwnd = hwnd; }
        // Only advertise WGC to the selector when the capture pipeline can actually run it.
        public bool GraphicsCaptureSupported => _owner.GraphicsCapturePipelineReady;
        public bool CanCaptureWindow(string windowId) => IsWindow(_hwnd) && !IsIconic(_hwnd);
        public bool PreferPrintWindowFallback(string windowId) => IsWindow(_hwnd);
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
