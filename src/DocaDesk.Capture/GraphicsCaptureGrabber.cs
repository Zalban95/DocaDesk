using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace DocaDesk.Capture;

/// <summary>One-shot Windows.Graphics.Capture grabber using a free-threaded frame pool (no DispatcherQueue).</summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class GraphicsCaptureGrabber : IDisposable
{
    private static readonly Guid Id3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private bool _disposed;

    private GraphicsCaptureGrabber(ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winrtDevice)
    {
        _device = device;
        _context = context;
        _winrtDevice = winrtDevice;
    }

    public static bool TryCreate(out GraphicsCaptureGrabber? grabber)
    {
        grabber = null;
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ||
                !GraphicsCaptureSession.IsSupported())
                return false;

            ID3D11Device device;
            try
            {
                device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            }
            catch
            {
                device = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport);
            }

            var context = device.ImmediateContext;
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            var hr = CaptureInterop.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winrtPtr);
            if (hr < 0 || winrtPtr == IntPtr.Zero)
            {
                device.Dispose();
                return false;
            }

            IDirect3DDevice winrtDevice;
            try
            {
                winrtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(winrtPtr);
            }
            finally
            {
                Marshal.Release(winrtPtr);
            }

            grabber = new GraphicsCaptureGrabber(device, context, winrtDevice);
            return true;
        }
        catch
        {
            grabber = null;
            return false;
        }
    }

    public Bitmap CaptureWindow(IntPtr hwnd, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var item = CaptureInterop.CreateItemForWindow(hwnd);
        return CaptureItem(item, timeout);
    }

    public Bitmap CaptureMonitor(IntPtr hMonitor, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var item = CaptureInterop.CreateItemForMonitor(hMonitor);
        return CaptureItem(item, timeout);
    }

    private Bitmap CaptureItem(GraphicsCaptureItem item, TimeSpan timeout)
    {
        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
            throw new InvalidOperationException("GraphicsCaptureItem has zero size (window minimized or unavailable).");

        var tcs = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;

        void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                var bmp = CopySurfaceToBitmap(frame.Surface, frame.ContentSize);
                // The pool keeps delivering until the finally below unsubscribes,
                // so every frame after the first found the result already set and
                // dropped a full-size Bitmap on the floor.
                if (!tcs.TrySetResult(bmp)) bmp.Dispose();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        try
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                size);
            pool.FrameArrived += OnFrameArrived;
            session = pool.CreateCaptureSession(item);
            // Do not set IsBorderRequired = false (§7.4).
            session.StartCapture();

            if (!tcs.Task.Wait(timeout))
                throw new TimeoutException($"Windows.Graphics.Capture produced no frame within {timeout.TotalSeconds:0}s.");

            return tcs.Task.GetAwaiter().GetResult();
        }
        finally
        {
            if (pool is not null)
                pool.FrameArrived -= OnFrameArrived;
            try { session?.Dispose(); } catch { /* ignore */ }
            try { pool?.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Serialises every use of the immediate context.
    ///
    /// `_context` is the device's ID3D11DeviceContext, created once and shared by
    /// the single ScreenCapturer the app holds. It is explicitly not
    /// thread-safe — and this method runs on a free-threaded frame-arrived
    /// callback (the pool is created with CreateFreeThreaded), so two concurrent
    /// screenshots interleave CopyResource/Map/Unmap on it. The mild outcome is
    /// one capture returning the other's pixels; the real one is
    /// DXGI_ERROR_DEVICE_REMOVED, after which _grabberInitTried keeps handing
    /// back the dead grabber and every capture silently falls through to GDI for
    /// the life of the process.
    ///
    /// The lock belongs here rather than around the capture as a whole: this is
    /// where the shared object is touched, it holds for a copy rather than for a
    /// multi-second wait, and a future caller of this method is covered by it
    /// without having to know why.
    /// </summary>
    private readonly object _contextGate = new();

    private Bitmap CopySurfaceToBitmap(IDirect3DSurface surface, SizeInt32 contentSize)
    {
        lock (_contextGate)
        {
            return CopySurfaceToBitmapCore(surface, contentSize);
        }
    }

    private Bitmap CopySurfaceToBitmapCore(IDirect3DSurface surface, SizeInt32 contentSize)
    {
        var texPtr = CaptureInterop.GetDxgiInterface(surface, Id3D11Texture2D);
        using var texture = new ID3D11Texture2D(texPtr);
        var desc = texture.Description;
        var width = contentSize.Width > 0 ? contentSize.Width : (int)desc.Width;
        var height = contentSize.Height > 0 ? contentSize.Height : (int)desc.Height;

        var stagingDesc = desc;
        stagingDesc.Usage = ResourceUsage.Staging;
        stagingDesc.BindFlags = BindFlags.None;
        stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDesc.MiscFlags = ResourceOptionFlags.None;
        stagingDesc.Width = (uint)width;
        stagingDesc.Height = (uint)height;

        using var staging = _device.CreateTexture2D(stagingDesc);
        _context.CopyResource(staging, texture);

        var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var srcPitch = (int)mapped.RowPitch;
                var dstPitch = bmpData.Stride;
                var rowBytes = width * 4;
                var srcBuf = new byte[srcPitch * height];
                Marshal.Copy(mapped.DataPointer, srcBuf, 0, Math.Min(srcBuf.Length, srcPitch * height));
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(srcBuf, y * srcPitch, bmpData.Scan0 + y * dstPitch, Math.Min(rowBytes, dstPitch));
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
            return bmp;
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { (_winrtDevice as IDisposable)?.Dispose(); } catch { /* ignore */ }
        _context.Dispose();
        _device.Dispose();
    }
}
