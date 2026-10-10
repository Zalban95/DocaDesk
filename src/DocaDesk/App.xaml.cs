using DocaDesk.Prompts;
using DocaDesk.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DocaDesk;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayHost? _tray;
    public static AppSession Session { get; } = new();
    public static McpHost Mcp { get; private set; } = null!;
    public static PromptCoordinator Prompts { get; private set; } = null!;
    /// <summary>The panel's look from the hub, drawn on the app's own windows (null in a window that has none yet).</summary>
    public static HubLook? Look { get; private set; }
    public static DispatcherQueue? UiDispatcher { get; private set; }
    /// <summary>Bring the main window to the front (a notification was clicked).</summary>
    public static Action? ShowMainWindow { get; private set; }
    /// <summary>Open a meeting's room in the panel window (a notification's or the tray's Join).</summary>
    public static Action<string>? OpenMeeting { get; set; }
    /// <summary>The last call that rang, for the tray's Join (ten minutes).</summary>
    public static (DocaDesk.Core.MeetingRing Ring, DateTime At)? LastRing { get; set; }
    /// <summary>The controlled-machine banner (meetings): shows itself while a person controls this computer.</summary>
    public static MeetingBanner? Banner { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine(e.Exception);
            e.Handled = true;
        };
    }

    /// <summary>
    /// An old version wrote the listener's URL — path secret and all — to
    /// <c>%LOCALAPPDATA%\DocaDesk\mcp-url.txt</c> in clear. That write is gone (see
    /// <see cref="OnLaunched"/>), but removing a write does not remove the file it already
    /// made: on this machine one was still sitting there months later holding the *current*
    /// secret, which until bearer enforcement arms is the listener's whole authentication.
    /// So delete it, every launch, best effort. ISSUES.md D-12.
    /// </summary>
    private static void DeleteLegacyMcpUrlFile()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DocaDesk", "mcp-url.txt");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* a file we cannot delete is not a reason to fail to start */ }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        UiDispatcher = DispatcherQueue.GetForCurrentThread();
        if (LookPreview.PathFrom(Environment.GetCommandLineArgs()) is { } previewPath)
        {
            _window = new MainWindow();
            Look = new HubLook(null);
            Look.Changed += l => UiDispatcher.TryEnqueue(() => LookApplier.Apply(l));
            LookPreview.Start(previewPath, Look);
            _window.ShowSettingsPreview();
            _window.Activate();
            return;
        }
        DeleteLegacyMcpUrlFile();
        NotificationService.EnsureRegistered();
        Mcp = new McpHost(Session);
        await Mcp.InitializeAsync();
        Session.Revoked += async _ => await Mcp.StopAsync();
        Prompts = new PromptCoordinator(Session);
        Banner = new MeetingBanner(Session);

        var startToTray = Environment.GetCommandLineArgs().Any(a =>
            string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase))
            || AppPrefs.StartMinimized;

        _window = new MainWindow();
        _tray = new TrayHost(_window) { BeforeQuit = () => Mcp.DisposeAsync().AsTask() };
        _window.AttachTray(_tray);
        ShowMainWindow = () => _tray?.ShowWindow();
        _window.BindMcp(Mcp);
        Look = new HubLook(Session);
        Look.Changed += l => UiDispatcher?.TryEnqueue(() => LookApplier.Apply(l));

        AppInstance.GetCurrent().Activated += OnActivated;

        await Session.InitializeAsync();
        await _window.BindSessionAsync(Session);

        var enableMcp = Environment.GetCommandLineArgs().Any(a =>
            string.Equals(a, "--mcp", StringComparison.OrdinalIgnoreCase));
        if (enableMcp)
        {
            foreach (var tool in McpHost.ToolNames)
                Mcp.SetConsent(tool, true);
            AppPrefs.McpAutoStart = true;
        }

        if ((enableMcp || AppPrefs.McpAutoStart) && Session.State == SessionState.Paired)
        {
            try
            {
                // Mcp.Url ends in the listener's path secret, and until the
                // panel is reached and bearer enforcement is turned on, that
                // secret is the whole authentication. It is DPAPI-protected in
                // the credential store and regex-redacted out of every log line;
                // writing it to %LOCALAPPDATA%\DocaDesk\mcp-url.txt in clear
                // undid both, and nothing in either repository ever read the
                // file back. The URL is on screen in the window for anyone who
                // needs it.
                await Mcp.StartAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("MCP auto-start failed: " + ex);
            }
        }

        if (startToTray)
            _window.AppWindow.Hide();
        else
            _window.Activate();
    }

    private void OnActivated(object? sender, AppActivationArguments e)
    {
        _window?.DispatcherQueue.TryEnqueue(() => _tray?.ShowWindow());
    }

    public void QuitFromTray() => _tray?.Quit();
}
