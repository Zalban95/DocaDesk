using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace DocaDesk.Capture;

/// <summary>Win32 → GraphicsCaptureItem via IGraphicsCaptureItemInterop (unpackaged-safe).</summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Direct3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var ptr = interop.CreateForWindow(hwnd, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hMonitor)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var ptr = interop.CreateForMonitor(hMonitor, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>Extract a DXGI/D3D interface from a WinRT Direct3D surface (CsWinRT-safe).</summary>
    public static IntPtr GetDxgiInterface(IDirect3DSurface surface, Guid iid)
    {
        try
        {
            var access = surface.As<IDirect3DDxgiInterfaceAccess>();
            return access.GetInterface(ref iid);
        }
        catch (Exception)
        {
            var abi = MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
            try
            {
                var accessIid = Direct3DDxgiInterfaceAccessIid;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, in accessIid, out var accessPtr));
                try
                {
                    var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetTypedObjectForIUnknown(
                        accessPtr, typeof(IDirect3DDxgiInterfaceAccess));
                    return access.GetInterface(ref iid);
                }
                finally
                {
                    Marshal.Release(accessPtr);
                }
            }
            finally
            {
                Marshal.Release(abi);
            }
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
}
