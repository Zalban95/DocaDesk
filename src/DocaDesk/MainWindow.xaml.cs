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
    private bool _prefsSync;
    private string? _editingServerId;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DocaDesk";
        ExtendsContentIntoTitleBar = true;
        ServerUrlBox.Text = AppSession.DefaultServerUrl;
        DeviceNameBox.Text = Environment.MachineName;
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
            var reg = _mcp.RegistrationMessage
                ?? (_mcp.Registration switch
                {
                    McpRegistrationState.WaitingForAccept => "Waiting to be accepted in the DOCA dashboard.",
                    McpRegistrationState.Registered => "Registered with DOCA for this device.",
                    _ => _mcp.IsRunning ? "Listener running." : "",
                });
            if (_mcp.BearerEnforced)
                reg += " Bearer auth enforced.";
            McpRegInfo.Message = reg.Trim();
            McpRegInfo.Severity = _mcp.Registration == McpRegistrationState.Registered
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Informational;
            McpRegInfo.IsOpen = McpRegInfo.Message.Length > 0;
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
        var res = Application.Current.Resources;
        Microsoft.UI.Xaml.Media.Brush Brush(string key) => (Microsoft.UI.Xaml.Media.Brush)res[key];
        var secondary = Brush("TextFillColorSecondaryBrush");

        var (state, dot) = view.State switch
        {
            McpServerState.Running => ($"Running · {view.ToolCount} tool{(view.ToolCount == 1 ? "" : "s")}", "SystemFillColorSuccessBrush"),
            McpServerState.Starting => ("Starting…", "SystemFillColorCautionBrush"),
            McpServerState.Error => ("Failed", "SystemFillColorCriticalBrush"),
            _ => ("Stopped", "SystemFillColorNeutralBrush"),
        };

        var panel = new StackPanel { Spacing = 4 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, Fill = Brush(dot), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = spec.Label, Style = (Style)res["BodyStrongTextBlockStyle"] });
        title.Children.Add(new TextBlock { Text = state, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock
        {
            Text = $"{spec.Command} {string.Join(' ', spec.Args)}".TrimEnd()
                + (string.IsNullOrWhiteSpace(spec.WorkingDirectory) ? "" : $"   (in {spec.WorkingDirectory})"),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            Foreground = secondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true,
        });
        if (!string.IsNullOrWhiteSpace(view.LastError))
            panel.Children.Add(new TextBlock { Text = view.LastError, FontSize = 12, Foreground = Brush("SystemFillColorCriticalBrush"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (running && view.ToolCount > 0 && spec.Consented)
            panel.Children.Add(new TextBlock { Text = string.Join(", ", view.ToolNames), FontSize = 12, Foreground = secondary, TextWrapping = TextWrapping.WrapWholeWords });

        var allow = new CheckBox { Content = "Allow its tools", IsChecked = spec.Consented };
        allow.Click += (_, _) => _mcp?.LocalServers.SetConsent(spec.Id, allow.IsChecked == true);

        var auto = new CheckBox { Content = "Start with listener", IsChecked = spec.AutoStart };
        auto.Click += (_, _) => _mcp?.LocalServers.SetAutoStart(spec.Id, auto.IsChecked == true);

        var power = new Button { Content = running ? "Stop" : "Start" };
        if (!running) power.Style = (Style)res["AccentButtonStyle"];
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

        var copyLog = new MenuFlyoutItem { Text = "Copy log" };
        copyLog.Click += (_, _) =>
        {
            if (_mcp is null) return;
            var lines = _mcp.LocalServers.LogOf(spec.Id);
            var package = new DataPackage();
            package.SetText(lines.Count == 0 ? "(no output)" : string.Join(Environment.NewLine, lines));
            Clipboard.SetContent(package);
            StatusBar.Text = $"{spec.Id}: log copied";
        };

        var edit = new Button { Content = "Edit" };
        edit.Click += (_, _) => BeginEditServer(spec);

        var remove = new MenuFlyoutItem { Text = "Remove…" };
        remove.Click += async (_, _) =>
        {
            if (_mcp is null) return;
            var dlg = new ContentDialog
            {
                Title = "Remove server",
                Content = $"Remove “{spec.Id}”? This stops it and deletes the definition.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            await _mcp.LocalServers.RemoveAsync(spec.Id);
            if (_editingServerId == spec.Id) ClearServerForm();
            RefreshLocalMcpUi();
        };

        var more = new MenuFlyout();
        more.Items.Add(copyLog);
        more.Items.Add(new MenuFlyoutSeparator());
        more.Items.Add(remove);

        var switches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        switches.Children.Add(allow);
        switches.Children.Add(auto);
        panel.Children.Add(switches);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(power);
        buttons.Children.Add(edit);
        buttons.Children.Add(new DropDownButton { Content = new FontIcon { Glyph = "", FontSize = 14 }, Flyout = more });

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(panel);
        grid.Children.Add(buttons);
        // The Card style lives on the window's root grid.
        return new Border { Style = (Style)((FrameworkElement)Content).Resources["Card"], Child = grid };
    }

    private void BeginEditServer(LocalMcpServerSpec spec)
    {
        _editingServerId = spec.Id;
        LocalMcpIdBox.Text = spec.Id;
        LocalMcpIdBox.IsEnabled = false;
        LocalMcpCommandBox.Text = spec.Command;
        LocalMcpArgsBox.Text = string.Join(Environment.NewLine, spec.Args);
        LocalMcpCwdBox.Text = spec.WorkingDirectory ?? "";
        LocalMcpFormTitle.Text = $"Edit {spec.Id}";
        LocalMcpSaveBtn.Content = "Save changes";
        LocalMcpError.Visibility = Visibility.Collapsed;
        LocalMcpForm.Visibility = Visibility.Visible;
        LocalMcpAddBtn.IsEnabled = false;
        LocalMcpForm.StartBringIntoView();
    }

    private void ClearServerForm()
    {
        _editingServerId = null;
        LocalMcpIdBox.Text = "";
        LocalMcpIdBox.IsEnabled = true;
        LocalMcpCommandBox.Text = "";
        LocalMcpArgsBox.Text = "";
        LocalMcpCwdBox.Text = "";
        LocalMcpFormTitle.Text = "Add server";
        LocalMcpSaveBtn.Content = "Save";
        LocalMcpError.Visibility = Visibility.Collapsed;
        LocalMcpForm.Visibility = Visibility.Collapsed;
        LocalMcpAddBtn.IsEnabled = true;
    }

    private void LocalMcpAdd_Click(object sender, RoutedEventArgs e)
    {
        ClearServerForm();
        LocalMcpForm.Visibility = Visibility.Visible;
        LocalMcpAddBtn.IsEnabled = false;
        LocalMcpIdBox.Focus(FocusState.Programmatic);
    }

    private void LocalMcpCancel_Click(object sender, RoutedEventArgs e) => ClearServerForm();

    private async void LocalMcpSave_Click(object sender, RoutedEventArgs e)
    {
        if (_mcp is null) return;
        LocalMcpError.Visibility = Visibility.Collapsed;
        try
        {
            var spec = new LocalMcpServerSpec
            {
                Id = LocalMcpIdBox.Text.Trim(),
                Label = LocalMcpIdBox.Text.Trim(),
                Command = LocalMcpCommandBox.Text.Trim(),
                Args = LocalMcpArgsBox.Text.Split('\n').Select(a => a.Trim()).Where(a => a.Length > 0).ToArray(),
                WorkingDirectory = string.IsNullOrWhiteSpace(LocalMcpCwdBox.Text) ? null : LocalMcpCwdBox.Text.Trim(),
            };
            if (_editingServerId is { } id)
                await _mcp.LocalServers.UpdateAsync(id, spec);
            else
                _mcp.LocalServers.Add(spec);
            ClearServerForm();
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
            // `??=` guarded the object but not the subscriptions, and this runs
            // on every session change. DashboardHost never unsubscribes, so the
            // two handler lists grew by one on every reconnect — and worse,
            // NavigateHome() ran again each time, throwing the user back to the
            // dashboard root mid-task because the network blinked. Build it once
            // or not at all.
            if (_dashboard is null)
            {
                _dashboard = new DashboardHost(DashboardView);
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
            }
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
        _prefsSync = true;
        try
        {
            AutostartToggle.IsOn = Autostart.IsEnabled();
            StartMinimizedToggle.IsOn = AppPrefs.StartMinimized;
            CloseToTrayToggle.IsOn = AppPrefs.CloseToTray;
            NotifyPromptsToggle.IsOn = AppPrefs.NotifyPrompts;
            NotifyAlertsToggle.IsOn = AppPrefs.NotifyAlerts;
        }
        finally
        {
            _prefsSync = false;
        }
        PrefsStatus.Visibility = Visibility.Collapsed;
        ServerUrlText.Text = _session?.ServerUrl.TrimEnd('/') ?? "";
        ShowOnly(settings: true);
        RefreshMcpUi();
    }

    /// <summary>Preferences save as they are toggled; there is no Save button to miss.</summary>
    private void Pref_Toggled(object sender, RoutedEventArgs e)
    {
        if (_prefsSync) return;
        AppPrefs.StartMinimized = StartMinimizedToggle.IsOn;
        AppPrefs.CloseToTray = CloseToTrayToggle.IsOn;
        AppPrefs.NotifyPrompts = NotifyPromptsToggle.IsOn;
        AppPrefs.NotifyAlerts = NotifyAlertsToggle.IsOn;
        if (!ReferenceEquals(sender, AutostartToggle)) return;
        try
        {
            Autostart.SetEnabled(AutostartToggle.IsOn);
            PrefsStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            PrefsStatus.Text = "Start with Windows: " + ex.Message;
            PrefsStatus.Visibility = Visibility.Visible;
        }
    }

    private void SettingsNav_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // Fires from InitializeComponent for the XAML IsSelected, before the sections exist.
        if (ActivitySection is null) return;
        var tag = sender.SelectedItem?.Tag as string;
        GeneralSection.Visibility = tag is "general" or null ? Visibility.Visible : Visibility.Collapsed;
        DeskSection.Visibility = tag == "desk" ? Visibility.Visible : Visibility.Collapsed;
        ServersSection.Visibility = tag == "servers" ? Visibility.Visible : Visibility.Collapsed;
        ActivitySection.Visibility = tag == "activity" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = false;
        _ = ApplyStateAsync();
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

    /* ── Title bar ────────────────────────────────────────
       ExtendsContentIntoTitleBar hands us the whole top strip, caption buttons
       included: they are drawn by the system over our content and we are the
       ones who have to stay out of their way.  Two things follow from that.
       SetTitleBar names the part of the strip that drags the window — without
       it the window cannot be moved by its title — and it must be an element
       with nothing interactive in it, because input inside that element goes to
       the drag handler rather than to the control.  And the caption buttons'
       width is not a constant: it changes with DPI, with maximise/restore, and
       it moves to the left edge under RTL.  AppWindow.TitleBar reports it, in
       physical pixels, so it is divided by the rasterization scale to land in
       the effective pixels XAML lays out in. */

    private void AppTitleBar_Loaded(object sender, RoutedEventArgs e)
    {
        SetTitleBar(TitleDragRegion);
        ApplyCaptionInset();
    }

    private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyCaptionInset();

    /// <summary>Keep the toolbar buttons clear of the system caption buttons.</summary>
    private void ApplyCaptionInset()
    {
        try
        {
            var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;
            // RTL puts the caption buttons on the left; then RightInset is 0 and
            // the toolbar is already clear, which is the right answer anyway.
            var inset = Math.Max(AppWindow.TitleBar.RightInset, 0) / scale;
            RightPaddingColumn.Width = new GridLength(inset);
        }
        catch
        {
            // No AppWindow (rare, during teardown): leave the last good value.
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => _dashboard?.Reload();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => _dashboard?.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => _dashboard?.ZoomOut();
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => _dashboard?.ZoomReset();
}
