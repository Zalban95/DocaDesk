using DocaDesk.Mcp;
using DocaDesk.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DocaDesk;

public sealed partial class MainWindow : Window
{
    private AppSession? _session;
    private DashboardHost? _dashboard;
    private TrayHost? _tray;
    private McpHost? _mcp;
    private bool _settingsOpen;
    private bool _mcpUiSync;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DocaDesk";
        ExtendsContentIntoTitleBar = true;
        ServerUrlBox.Text = AppSession.DefaultServerUrl;
        DeviceNameBox.Text = Environment.MachineName;
        AutostartToggle.IsOn = Autostart.IsEnabled();
    }

    public void AttachTray(TrayHost tray) => _tray = tray;

    public async Task BindSessionAsync(AppSession session)
    {
        _session = session;
        _session.Changed += () => DispatcherQueue.TryEnqueue(() => _ = ApplyStateAsync());
        ServerUrlBox.Text = _session.ServerUrl;
        await ApplyStateAsync();
    }

    public void BindMcp(McpHost mcp)
    {
        _mcp = mcp;
        _mcp.Changed += () => DispatcherQueue.TryEnqueue(RefreshMcpUi);
        RefreshMcpUi();
    }

    private void RefreshMcpUi()
    {
        if (_mcp is null) return;
        _mcpUiSync = true;
        try
        {
            McpListenerToggle.IsOn = _mcp.IsRunning;
            McpUrlBox.Text = _mcp.Url ?? "(listener off — enable above; requires Tailscale IPv4)";
            McpRegStatus.Text = _mcp.RegistrationMessage
                ?? (_mcp.Registration switch
                {
                    McpRegistrationState.WaitingForAccept => "Waiting to be accepted in the DOCA dashboard.",
                    McpRegistrationState.Registered => "Registered with DOCA for this device.",
                    _ => _mcp.IsRunning ? "Listener running." : "",
                });
            if (_mcp.BearerEnforced)
                McpRegStatus.Text += " Bearer auth enforced.";
            SyncConsentToggle(ToolListWindows, "list_windows");
            SyncConsentToggle(ToolScreenshot, "screenshot");
            SyncConsentToggle(ToolGetClip, "get_clipboard_text");
            SyncConsentToggle(ToolSetClip, "set_clipboard_text");
            SyncConsentToggle(ToolOpenUrl, "open_url");
            RefreshLocalMcpUi();
            AuditList.ItemsSource = _mcp.Audit.Snapshot()
                .Reverse()
                .Take(40)
                .Select(e => $"{e.At:HH:mm:ss}  {e.Kind}  {e.Summary}")
                .ToList();

            if (_mcp.IsRunning)
                _tray?.SetTooltip($"DocaDesk — MCP on");
        }
        finally
        {
            _mcpUiSync = false;
        }
    }

    private void SyncConsentToggle(ToggleSwitch sw, string name)
    {
        if (_mcp is null) return;
        sw.IsOn = _mcp.Consent.IsEnabled(name);
    }

    private void RefreshLocalMcpUi()
    {
        if (_mcp is null) return;
        var views = _mcp.LocalServers.Views();
        LocalMcpRows.Children.Clear();
        LocalMcpEmpty.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var view in views)
            LocalMcpRows.Children.Add(BuildLocalMcpRow(view));
    }

    /// <summary>
    /// Built in code rather than a DataTemplate: every control on the row closes
    /// over one server, and a closure the compiler checks cannot fail the way a
    /// mistyped binding path does.
    /// </summary>
    private UIElement BuildLocalMcpRow(LocalMcpServerView view)
    {
        var spec = view.Spec;
        var running = view.State == McpServerState.Running;
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };

        var state = view.State switch
        {
            McpServerState.Running => $"running · {view.ToolCount} tool{(view.ToolCount == 1 ? "" : "s")}",
            McpServerState.Starting => "starting",
            McpServerState.Error => "failed",
            _ => "stopped",
        };
        panel.Children.Add(new TextBlock
        {
            Text = $"{spec.Label} — {state}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{spec.Command} {string.Join(' ', spec.Args)}".TrimEnd(),
            Opacity = 0.7,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(view.LastError))
            panel.Children.Add(new TextBlock { Text = view.LastError, Opacity = 0.9, FontSize = 12, TextWrapping = TextWrapping.WrapWholeWords });
        if (running && view.ToolCount > 0 && spec.Consented)
            panel.Children.Add(new TextBlock { Text = string.Join(", ", view.ToolNames), Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.WrapWholeWords });

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var allow = new CheckBox { Content = "Allow its tools", IsChecked = spec.Consented };
        allow.Click += (_, _) => _mcp?.LocalServers.SetConsent(spec.Id, allow.IsChecked == true);

        var auto = new CheckBox { Content = "Start with listener", IsChecked = spec.AutoStart };
        auto.Click += (_, _) => _mcp?.LocalServers.SetAutoStart(spec.Id, auto.IsChecked == true);

        var power = new Button { Content = running ? "Stop" : "Start" };
        power.Click += async (_, _) =>
        {
            if (_mcp is null) return;
            power.IsEnabled = false;
            try
            {
                if (running)
                    await _mcp.LocalServers.StopAsync(spec.Id);
                else if (!await _mcp.LocalServers.StartAsync(spec.Id))
                    StatusBar.Text = $"MCP {spec.Id}: "
                        + (_mcp.LocalServers.Views().FirstOrDefault(v => v.Spec.Id == spec.Id)?.LastError ?? "did not start — see Copy log");
            }
            finally
            {
                power.IsEnabled = true;
            }
            RefreshLocalMcpUi();
        };

        var copyLog = new Button { Content = "Copy log" };
        copyLog.Click += (_, _) =>
        {
            if (_mcp is null) return;
            var lines = _mcp.LocalServers.LogOf(spec.Id);
            var package = new DataPackage();
            package.SetText(lines.Count == 0 ? "(no output)" : string.Join(Environment.NewLine, lines));
            Clipboard.SetContent(package);
            StatusBar.Text = $"{spec.Id}: log copied";
        };

        var remove = new Button { Content = "Remove" };
        remove.Click += async (_, _) =>
        {
            if (_mcp is null) return;
            await _mcp.LocalServers.RemoveAsync(spec.Id);
            RefreshLocalMcpUi();
        };

        controls.Children.Add(allow);
        controls.Children.Add(auto);
        controls.Children.Add(power);
        controls.Children.Add(copyLog);
        controls.Children.Add(remove);
        panel.Children.Add(controls);
        return panel;
    }

    private void LocalMcpAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_mcp is null) return;
        LocalMcpError.Visibility = Visibility.Collapsed;
        try
        {
            var id = LocalMcpIdBox.Text.Trim();
            _mcp.LocalServers.Add(new LocalMcpServerSpec
            {
                Id = id,
                Label = id,
                Command = LocalMcpCommandBox.Text.Trim(),
                Args = LocalMcpArgsBox.Text.Split('\n').Select(a => a.Trim()).Where(a => a.Length > 0).ToArray(),
            });
            LocalMcpIdBox.Text = "";
            LocalMcpCommandBox.Text = "";
            LocalMcpArgsBox.Text = "";
            RefreshLocalMcpUi();
        }
        catch (Exception ex)
        {
            LocalMcpError.Text = ex.Message;
            LocalMcpError.Visibility = Visibility.Visible;
        }
    }

    private async Task ApplyStateAsync()
    {
        if (_session is null) return;

        var mcpBit = _mcp?.IsRunning == true
            ? (_mcp.Registration == McpRegistrationState.WaitingForAccept ? " · MCP waiting accept" : " · MCP on")
            : "";
        StatusBar.Text = $"{_session.State} · {_session.ServerUrl.TrimEnd('/')}{mcpBit}";
        _tray?.SetTooltip($"DocaDesk — {_session.State}{mcpBit}");

        if (_settingsOpen)
        {
            ShowOnly(settings: true);
            RefreshMcpUi();
            return;
        }

        switch (_session.State)
        {
            case SessionState.Unpaired:
            case SessionState.Pairing:
                ShowOnly(pair: true);
                PairError.Visibility = string.IsNullOrEmpty(_session.LastError) ? Visibility.Collapsed : Visibility.Visible;
                PairError.Text = _session.LastError ?? "";
                break;

            case SessionState.Offline:
                ShowOnly(status: true);
                StatusTitle.Text = "Server unreachable";
                StatusBody.Text = _session.LastError
                    ?? $"Cannot reach {_session.ServerUrl}. Check the URL and your tailnet connection.";
                StatusRetryBtn.Visibility = Visibility.Visible;
                StatusUnpairBtn.Visibility = Visibility.Visible;
                break;

            case SessionState.Revoked:
                ShowOnly(status: true);
                StatusTitle.Text = "Device revoked";
                StatusBody.Text = _session.LastError ?? "This device's token was revoked. Pair again.";
                StatusRetryBtn.Visibility = Visibility.Collapsed;
                StatusUnpairBtn.Visibility = Visibility.Visible;
                break;

            case SessionState.Paired:
                ShowOnly(dashboard: true);
                await EnsureDashboardAsync();
                break;
        }
    }

    private async Task EnsureDashboardAsync()
    {
        if (_session is null) return;
        try
        {
            _dashboard ??= new DashboardHost(DashboardView);
            _dashboard.NavigationFailed += msg => DispatcherQueue.TryEnqueue(() =>
            {
                ShowOnly(status: true);
                StatusTitle.Text = "Dashboard offline";
                StatusBody.Text = msg;
                StatusRetryBtn.Visibility = Visibility.Visible;
                StatusUnpairBtn.Visibility = Visibility.Collapsed;
            });
            _dashboard.NavigationSucceeded += () => DispatcherQueue.TryEnqueue(() =>
            {
                if (_session?.State == SessionState.Paired && !_settingsOpen)
                    ShowOnly(dashboard: true);
            });
            await _dashboard.InitializeAsync(new Uri(_session.ServerUrl));
            _dashboard.NavigateHome();
            TitleText.Text = "DocaDesk";
        }
        catch (Exception ex)
        {
            ShowOnly(status: true);
            StatusTitle.Text = "WebView2 unavailable";
            StatusBody.Text = ex.Message;
            StatusRetryBtn.Visibility = Visibility.Visible;
            StatusUnpairBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowOnly(bool pair = false, bool dashboard = false, bool status = false, bool settings = false)
    {
        PairPanel.Visibility = pair ? Visibility.Visible : Visibility.Collapsed;
        DashboardView.Visibility = dashboard ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Visibility = status ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        var tools = dashboard;
        ZoomInBtn.IsEnabled = tools;
        ZoomOutBtn.IsEnabled = tools;
        ZoomResetBtn.IsEnabled = tools;
        ReloadBtn.IsEnabled = tools;
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        PairBtn.IsEnabled = false;
        PairError.Visibility = Visibility.Collapsed;
        try
        {
            await _session.SetServerUrlAsync(ServerUrlBox.Text.Trim());
            var pin = string.IsNullOrWhiteSpace(PinBox.Text) ? null : PinBox.Text.Trim();
            await _session.PairAsync(PairCodeBox.Text, DeviceNameBox.Text, pin);
        }
        catch (Exception ex)
        {
            PairError.Text = ex.Message;
            PairError.Visibility = Visibility.Visible;
        }
        finally
        {
            PairBtn.IsEnabled = true;
        }
    }

    private async void ProbePin_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        try
        {
            await _session.SetServerUrlAsync(ServerUrlBox.Text.Trim());
            var fp = await _session.ProbeCertificateFingerprintAsync();
            PinBox.Text = fp ?? "";
            PairError.Text = fp is null ? "Could not read certificate." : "Fingerprint filled in — pair only if you trust this server.";
            PairError.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            PairError.Text = ex.Message;
            PairError.Visibility = Visibility.Visible;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        await _session.RefreshConnectionAsync();
    }

    private async void Unpair_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        _settingsOpen = false;
        if (_mcp is not null)
            await _mcp.StopAsync();
        await _session.UnpairAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = true;
        AutostartToggle.IsOn = Autostart.IsEnabled();
        StartMinimizedToggle.IsOn = AppPrefs.StartMinimized;
        NotifyPromptsToggle.IsOn = AppPrefs.NotifyPrompts;
        NotifyAlertsToggle.IsOn = AppPrefs.NotifyAlerts;
        ShowOnly(settings: true);
        RefreshMcpUi();
    }

    private void StartMinimized_Toggled(object sender, RoutedEventArgs e)
    {
        if (_mcpUiSync) return;
        AppPrefs.StartMinimized = StartMinimizedToggle.IsOn;
    }

    private void NotifyPref_Toggled(object sender, RoutedEventArgs e)
    {
        if (_mcpUiSync) return;
        if (sender is ToggleSwitch sw && sw.Tag is string tag)
        {
            if (tag == "prompt") AppPrefs.NotifyPrompts = sw.IsOn;
            if (tag == "alert") AppPrefs.NotifyAlerts = sw.IsOn;
        }
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = false;
        _ = ApplyStateAsync();
    }

    private void Autostart_Toggled(object sender, RoutedEventArgs e)
    {
        try { Autostart.SetEnabled(AutostartToggle.IsOn); }
        catch (Exception ex)
        {
            StatusBar.Text = "Autostart: " + ex.Message;
        }
    }

    private async void McpListener_Toggled(object sender, RoutedEventArgs e)
    {
        if (_mcpUiSync || _mcp is null) return;
        try
        {
            if (McpListenerToggle.IsOn)
            {
                await _mcp.StartAsync();
                AppPrefs.McpAutoStart = true;
            }
            else
            {
                await _mcp.StopAsync();
                AppPrefs.McpAutoStart = false;
            }
        }
        catch (Exception ex)
        {
            McpListenerToggle.IsOn = false;
            StatusBar.Text = "MCP: " + ex.Message;
        }
        RefreshMcpUi();
    }

    private void ToolConsent_Toggled(object sender, RoutedEventArgs e)
    {
        if (_mcpUiSync || _mcp is null) return;
        if (sender is ToggleSwitch sw && sw.Tag is string name)
            _mcp.SetConsent(name, sw.IsOn);
    }

    private async void McpCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(McpUrlBox.Text) || McpUrlBox.Text.StartsWith('('))
            return;
        var package = new DataPackage();
        package.SetText(McpUrlBox.Text);
        Clipboard.SetContent(package);
        StatusBar.Text = "MCP URL copied";
        await Task.CompletedTask;
    }

    private async void McpRegen_Click(object sender, RoutedEventArgs e)
    {
        if (_mcp is null) return;
        await _mcp.RegenerateSecretAsync();
        RefreshMcpUi();
    }

    private void Metrics_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var win = new MetricsPanelWindow(_session);
        win.Activate();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _dashboard?.Reload();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => _dashboard?.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => _dashboard?.ZoomOut();
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => _dashboard?.ZoomReset();
}
