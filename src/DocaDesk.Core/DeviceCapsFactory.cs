using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DocaDesk.Core.Models;

namespace DocaDesk.Core;

/// <summary>Builds honest desktop caps from the real machine (§3.4).</summary>
public static class DeviceCapsFactory
{
    public static DeviceCaps FromMachine()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DeviceCaps
            {
                FormFactor = "desktop",
                Screen = new ScreenCaps { W = 1920, H = 1080, Dpr = 1, Shape = "rect", Color = true },
                Input = new InputCaps { Touch = false, Voice = false, Text = true, Camera = false, Buttons = true },
                Render = ["svg", "svg.smil", "image", "text"],
                Motion = ["1"],
                Exec = [],
                Sensors = [],
            };
        }

        return FromWindowsMachine();
    }

    [SupportedOSPlatform("windows")]
    private static DeviceCaps FromWindowsMachine()
    {
        var (w, h, dpr) = PrimaryDisplay();
        var touch = GetSystemMetrics(0x4B /* SM_DIGITIZER */) != 0;
        var camera = false; // do not declare unless we implement camera capture
        var sensors = TryReadBatteryPercent(out _) ? new List<string> { "battery" } : [];
        return new DeviceCaps
        {
            FormFactor = "desktop",
            Screen = new ScreenCaps
            {
                W = w,
                H = h,
                Dpr = dpr,
                Shape = "rect",
                Color = true,
            },
            Input = new InputCaps
            {
                Touch = touch,
                Voice = false,
                Text = true,
                Camera = camera,
                Buttons = true,
            },
            Render = ["svg", "svg.smil", "image", "text"],
            Motion = ["1"],
            Exec = [],
            Sensors = sensors,
        };
    }

    /// <summary>Reads AC/battery percent when a system battery is present (§3.4).</summary>
    [SupportedOSPlatform("windows")]
    public static bool TryReadBatteryPercent(out int percent)
    {
        percent = 0;
        try
        {
            if (!GetSystemPowerStatus(out var status))
                return false;
            if (status.BatteryFlag == 128 || status.BatteryLifePercent == 255)
                return false;
            percent = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static (int W, int H, double Dpr) PrimaryDisplay()
    {
        try
        {
            var w = GetSystemMetrics(0); // SM_CXSCREEN
            var h = GetSystemMetrics(1); // SM_CYSCREEN
            var dpi = 96;
            try
            {
                dpi = (int)GetDpiForSystem();
            }
            catch
            {
                // older OS without GetDpiForSystem
            }
            var dpr = Math.Round(dpi / 96.0, 2);
            if (w <= 0 || h <= 0)
                return (1920, 1080, 1.0);
            return (w, h, dpr);
        }
        catch
        {
            return (1920, 1080, 1.0);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus sps);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
