using System.Drawing;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DocaDesk.Services;

/// <summary>Tray icon and close policy. X quits unless CloseToTray is on.</summary>
public sealed class TrayHost : IDisposable
{
    private readonly Window _window;
    private readonly TaskbarIcon _tray;
    private bool _quitRequested;
    private bool _disposed;
    private bool _quitting;

    /// <summary>Stop the MCP listener and local servers before the process exits.</summary>
    public Func<Task>? BeforeQuit { get; set; }

    public TrayHost(Window window)
    {
        _window = window;

        var showCmd = new XamlUICommand { Label = "Open DocaDesk" };
        var quitCmd = new XamlUICommand { Label = "Quit" };
        showCmd.ExecuteRequested += (_, _) => ShowWindow();
        quitCmd.ExecuteRequested += (_, _) => Quit();

        _tray = new TaskbarIcon
        {
            ToolTipText = "DocaDesk",
            NoLeftClickDelay = true,
            LeftClickCommand = showCmd,
            ContextFlyout = new MenuFlyout
            {
                Items =
                {
                    new MenuFlyoutItem { Text = "Open DocaDesk", Command = showCmd },
                    new MenuFlyoutSeparator(),
                    new MenuFlyoutItem { Text = "Quit", Command = quitCmd },
                },
            },
        };

        TrySetIcon();
        _tray.ForceCreate();
        _window.AppWindow.Closing += OnClosing;
    }

    public void SetTooltip(string text) => _tray.ToolTipText = text;

    private void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_quitRequested)
            return;
        if (AppPrefs.CloseToTray)
        {
            args.Cancel = true;
            _window.AppWindow.Hide();
            return;
        }
        args.Cancel = true;
        Quit();
    }

    public void ShowWindow()
    {
        _window.AppWindow.Show();
        _window.Activate();
    }

    public async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _quitRequested = true;
        try
        {
            if (BeforeQuit is not null)
                await BeforeQuit().ConfigureAwait(true);
        }
        catch { /* shutting down anyway */ }
        Dispose();
        _window.Close();
        Application.Current?.Exit();
    }

    private void TrySetIcon()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DocaDesk",
                "tray.ico");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
                WriteSimpleIcon(path);
            _tray.Icon = new Icon(path);
        }
        catch
        {
            // Tray still works with the default icon.
        }
    }

    private static void WriteSimpleIcon(string path)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 24, 90, 140));
            using var brush = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("D", font, brush, 8, 6);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            using var fs = File.Create(path);
            icon.Save(fs);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _window.AppWindow.Closing -= OnClosing; } catch { /* ignore */ }
        _tray.Dispose();
    }
}
