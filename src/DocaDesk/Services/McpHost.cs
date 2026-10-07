using System.Security.Cryptography;
using System.Text.Json;
using DocaDesk.Capture;
using DocaDesk.Core.Audit;
using DocaDesk.Core.Logging;
using DocaDesk.Core.Models;
using DocaDesk.Core.Security;
using DocaDesk.Mcp;

namespace DocaDesk.Services;

public enum McpRegistrationState
{
    /// <summary>Listener off or not yet reconciled.</summary>
    Idle,
    /// <summary>Offer recorded; waiting for a human to accept in the dashboard.</summary>
    WaitingForAccept,
    /// <summary>Accepted entry exists on the host for this device.</summary>
    Registered,
}

/// <summary>Owns MCP listener lifecycle, secret, consent, audit, and DOCA 2.10 mcp/self reconciliation.</summary>
public sealed class McpHost : IAsyncDisposable
{
    private readonly AppSession _session;
    private readonly ICredentialStore _creds;
    private readonly IDocaLogger _log;
    private readonly AuditLog _audit = new();
    private readonly ScreenCapturer _capturer = new();
    private readonly ToolConsent _consent = new();
    private readonly LocalMcpRegistry _localServers;
    private readonly FamilyConsent _families;
    private readonly DeviceHands _hands;
    private McpHttpListener? _listener;
    private string _secret = McpHttpListener.NewPathSecret();
    private string _bearer = NewBearerToken();
    private bool _enforceBearer;
    private CancellationTokenSource? _waitPollCts;
    private SealKey? _sealKey;
    private readonly SealedSecrets _sealed;

    public McpHost(AppSession session, ICredentialStore? creds = null, IDocaLogger? log = null)
    {
        _session = session;
        _creds = creds ?? (OperatingSystem.IsWindows() ? new DpapiCredentialStore() : new MemoryCredentialStore());
        _log = log ?? new RedactingLogger();
        _localServers = new LocalMcpRegistry(LocalMcpRegistry.DefaultStorePath(), _consent, _audit);
        // Tell DOCA too: it holds our event stream open and lists the tools again (DOCA 2.90.0+).
        _localServers.ToolsChanged += () => { Changed?.Invoke(); _listener?.NotifyToolsChanged(); };

        // Devices as hands (§22.1). The consent lives in HKCU like every other app-local boolean,
        // but FamilyConsent itself knows nothing about the registry — the Linux client supplies its
        // own two delegates and reuses the rest of the file unchanged.
        _families = new FamilyConsent(AppPrefs.GetFamilyGrant, AppPrefs.SetFamilyGrant);
        _hands = new DeviceHands(() => _session.Client, _families, _audit)
        {
            OnReconnect = ct => _session.RefreshConnectionAsync(ct),
            // Stay paired: the token is kept. Forgetting it is `revoked`, a different event. And not
            // StopAsync, which clears the person's master switch (D-23).
            OnDisconnect = _ => DisconnectAsync(),
            // §22.1 wants `refresh` to re-report caps as well, via PATCH /devices/{id}. That route
            // has no client method yet (caps are sent once, at pair time), so this re-reads the
            // server's capabilities and reopens the connection, and the grants half — the half the
            // harness actually gates on — is reported by DeviceHands either way. TODO.md records
            // the missing PATCH so the ack's wording stops being a half-truth.
            OnRefreshCaps = ct => _session.RefreshConnectionAsync(ct),
        };
        _families.Changed += () =>
        {
            // A family the person just allowed changes what this listener offers, so DOCA has to
            // re-list — the same reason a local server starting does (D-8).
            _listener?.NotifyToolsChanged();
            Changed?.Invoke();
        };
        _hands.Changed += () => Changed?.Invoke();

        // DeviceHands reports on every grant change itself (D-19); this adds "whenever the session
        // comes back paired". DOCA offers the
        // harness nothing from a device that has not reported, and a report also clears a
        // disconnect on its side (devices-control.js:91) — so this is how the device says it is
        // back, which is why it hangs off the session rather than off the listener.
        // Sealed secrets (§22.3): used through the input family — the one the person lent for typing — on this
        // machine's keyboard, its clipboard or the panel's own password field; never read by the agent.
        _sealed = new SealedSecrets(
            () => _sealKey,
            () => _families.IsUsable(ToolFamilies.Input) ? null
                : "This machine does not lend its input family (typing and the clipboard), so it takes no secrets. Turn it on in DocaDesk → Settings → This device.",
            new WindowsSecretSink(() => SecretField),
            _audit);

        _session.Changed += OnSessionChanged;
        _session.EventReceived += OnPushEventAsync;
    }

    private SessionState _lastReportedState = SessionState.Unpaired;

    private void OnSessionChanged()
    {
        var state = _session.State;
        if (state == _lastReportedState) return;
        _lastReportedState = state;
        if (state == SessionState.Paired)
        {
            _ = _hands.ReportGrantsAsync();
            _ = TakeSealKeyAsync();
        }
    }

    /// <summary>
    /// Take the hub's seal key for this device (§22.3): asked on every connect — the same key while paired, a new one
    /// for a new pairing — and kept with DPAPI, so a hub that is briefly unreachable still finds the key here. A hub
    /// without sealed secrets answers 404 and nothing changes.
    /// </summary>
    private async Task TakeSealKeyAsync()
    {
        try
        {
            var r = _session.Client is { } c ? await c.GetSealKeyAsync().ConfigureAwait(false) : null;
            if (r?.Key is not { Length: > 0 } || string.IsNullOrEmpty(r.Aad)) return;
            var key = new SealKey(Convert.FromBase64String(r.Key), r.Aad);
            if (key.Key.Length != 32) return;
            _sealKey = key;
            await _creds.SaveAsync(CredentialKeys.McpSealKey, key.ToStored()).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.Warn("Seal key not taken: " + ex.GetType().Name); }
    }

    /// <summary>The panel's page, for a secret that belongs to a site (set by the window that shows it).</summary>
    public ISecretField? SecretField { get; set; }

    /// <summary>Sealed secrets on this machine: the hidden tool and the read hold after a use.</summary>
    public SealedSecrets Sealed => _sealed;

    /// <summary>What this machine's person has allowed the harness to do here, per family (§22.1).</summary>
    public FamilyConsent Families => _families;

    /// <summary>Reporting grants, and carrying out `device.control`.</summary>
    public DeviceHands Hands => _hands;

    public AuditLog Audit => _audit;
    public ToolConsent Consent => _consent;
    /// <summary>The MCP servers running on this machine, whose tools this listener forwards.</summary>
    public LocalMcpRegistry LocalServers => _localServers;
    public bool IsRunning => _listener?.IsRunning == true;
    public string? Url => _listener?.BoundUrl;
    public string? LastError => _listener?.LastError;
    public McpRegistrationState Registration { get; private set; } = McpRegistrationState.Idle;
    public string? RegistrationMessage { get; private set; }
    public bool BearerEnforced => _enforceBearer;
    public event Action? Changed;

    public static readonly string[] ToolNames =
    [
        "list_windows", "screenshot", "get_clipboard_text", "set_clipboard_text", "open_url",
    ];

    public async Task InitializeAsync()
    {
        var existing = await _creds.LoadAsync(CredentialKeys.McpPathSecret);
        if (!string.IsNullOrEmpty(existing))
            _secret = existing;
        else
            await _creds.SaveAsync(CredentialKeys.McpPathSecret, _secret);

        var bearer = await _creds.LoadAsync(CredentialKeys.McpBearerToken);
        if (!string.IsNullOrEmpty(bearer))
            _bearer = bearer;
        else
            await _creds.SaveAsync(CredentialKeys.McpBearerToken, _bearer);

        foreach (var tool in ToolNames)
            _consent.Set(tool, AppPrefs.GetToolConsent(tool));

        _sealKey = SealKey.FromStored(await _creds.LoadAsync(CredentialKeys.McpSealKey));
    }

    public void SetConsent(string tool, bool enabled)
    {
        _consent.Set(tool, enabled);
        AppPrefs.SetToolConsent(tool, enabled);
    }

    /// <summary>
    /// A consented local MCP server counts even while it is stopped: a machine
    /// may exist to forward one filesystem server and never consent to a
    /// screenshot, and autostart happens after the listener is up.
    /// </summary>
    public bool AnyToolConsented() =>
        ToolNames.Any(t => _consent.IsEnabled(t))
        || _localServers.List().Any(s => s.Consented)
        || ToolFamilies.All.Any(_families.IsUsable);   // a machine may grant only its files

    public async Task StartAsync()
    {
        await StopListenerOnlyAsync().ConfigureAwait(false);
        var host = new Uri(_session.ServerUrl).Host;
        // The five desk tools are static; the families (§22.1) are asked on every list and call
        // alongside the local servers, so a family appears the moment it is granted and disappears
        // the moment it is refused or revoked (D-17). A family is one question asked once, so its
        // tools gate on FamilyConsent and are registered in ToolConsent as always-on.
        var tools = DeskTools.Create(_capturer, () => _session.Client, _audit, OnCaptureFlash);
        var familyTools = FilesTools.Create(_families, _audit)
            .Concat(ShellTools.Create(_families, _audit))
            .Concat(ProcessAppTools.Create(_families, _audit))
            .Concat(WindowsTools.Create(_families, _audit))
            .Concat(DeskTools.CreateScreenFamily(_capturer, () => _session.Client, _audit, OnCaptureFlash, _families))
            .ToArray();
        foreach (var t in familyTools)
            _consent.Register(t.Name, true);
        _listener = new McpHttpListener(new McpListenerOptions
        {
            PathSecret = _secret,
            AllowedRemoteHost = host,
            Consent = _consent,
            Audit = _audit,
            Tools = tools,
            // Asked per request, so a local server that stops, or a family that is revoked, also
            // stops being offered.
            DynamicTools = () => _localServers.Tools().Concat(FamilyTool.Offered(familyTools, _families)).ToArray(),
            RequiredBearerToken = _bearer,
            Sealed = _sealed,
            // Offer/patch the header first; enforce only after host is known to hold it (§3.4).
            EnforceBearer = _enforceBearer,
        });
        try
        {
            await _listener.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            Changed?.Invoke();
            throw;
        }

        AppPrefs.McpAutoStart = true;
        // Only now: a local server is worth spawning once something can reach its tools.
        await _localServers.StartAutoStartAsync().ConfigureAwait(false);
        await ReconcileRegistrationAsync(offerOn404: true).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        StopWaitPoll();
        AppPrefs.McpAutoStart = false;
        Registration = McpRegistrationState.Idle;
        RegistrationMessage = null;
        await StopListenerOnlyAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>
    /// DOCA's `disconnect` (design §5): stop offering anything until the person opens DocaDesk
    /// again, and stay paired. Deliberately not <see cref="StopAsync"/>, which is the person's own
    /// switch and clears <c>McpAutoStart</c> — a host request must not rewrite that choice, so a
    /// relaunch brings everything back exactly as it was (D-23).
    /// </summary>
    public async Task DisconnectAsync()
    {
        StopWaitPoll();
        await StopListenerOnlyAsync().ConfigureAwait(false);
        await _localServers.StopAllAsync().ConfigureAwait(false);
        ShellTools.StopAllJobs();
        Registration = McpRegistrationState.Idle;
        RegistrationMessage = "Disconnected by DOCA — reopen DocaDesk to reconnect. Your settings are unchanged.";
        _audit.Add("device.disconnect", "listener, local servers and background jobs stopped; settings kept");
        Changed?.Invoke();
    }

    public async Task RegenerateSecretAsync()
    {
        var was = IsRunning;
        await StopListenerOnlyAsync().ConfigureAwait(false);
        _secret = McpHttpListener.NewPathSecret();
        await _creds.SaveAsync(CredentialKeys.McpPathSecret, _secret).ConfigureAwait(false);
        // New path ⇒ new bearer so an old header cannot unlock the new URL.
        _bearer = NewBearerToken();
        _enforceBearer = false;
        await _creds.SaveAsync(CredentialKeys.McpBearerToken, _bearer).ConfigureAwait(false);
        if (was)
            await StartAsync().ConfigureAwait(false);
        else
            Changed?.Invoke();
    }

    /// <summary>
    /// Single reconciliation path for §3.1 / §3.2: GET self → offer on 404, PATCH on URL/header drift.
    /// </summary>
    /// <param name="offerOn404">
    /// When true (start / regen), POST an offer on 404. When false (wait-poll), only observe so we
    /// do not replace a pending offer every few seconds.
    /// </param>
    public async Task ReconcileRegistrationAsync(CancellationToken ct = default, bool offerOn404 = true)
    {
        var client = _session.Client;
        var url = _listener?.BoundUrl;
        if (client is null || string.IsNullOrEmpty(url))
            return;

        var headers = AuthHeaders();
        try
        {
            var self = await client.GetMcpSelfAsync(ct).ConfigureAwait(false);
            if (self is null)
            {
                if (!offerOn404)
                {
                    Registration = McpRegistrationState.WaitingForAccept;
                    RegistrationMessage ??=
                        "Waiting to be accepted in the DOCA dashboard. Not an error.";
                    Changed?.Invoke();
                    return;
                }

                var offer = await client.OfferMcpAsync(new McpOfferRequest
                {
                    Label = Environment.MachineName,
                    Url = url,
                    Headers = headers,
                    Tools = OfferedToolNames(),
                    Note = "Windows desktop tools (DocaDesk)",
                }, ct).ConfigureAwait(false);
                Registration = McpRegistrationState.WaitingForAccept;
                RegistrationMessage =
                    $"Waiting to be accepted in the DOCA dashboard (offer {offer.Id ?? "pending"}). Not an error.";
                _log.Info("MCP offer submitted; waiting for dashboard accept");
                _audit.Add("mcp.offer", RegistrationMessage);
                EnsureWaitPoll();
                Changed?.Invoke();
                return;
            }

            StopWaitPoll();

            // Always PATCH. GET values are masked, so a rotated bearer looks
            // identical to a matching one — skipping the write is how a live
            // listener answers 404 to the host that still holds the old token.
            self = await client.PatchMcpSelfAsync(new McpSelfPatchRequest
            {
                Url = url,
                Headers = headers,
            }, ct).ConfigureAwait(false);
            _log.Info("MCP self address/headers patched");
            _audit.Add("mcp.patch", "Updated host entry for this device");

            Registration = McpRegistrationState.Registered;
            RegistrationMessage = $"Registered on host as {self.Id ?? self.Label ?? "mcp server"}.";

            // Safe to enforce once the host record carries Authorization (§3.4).
            // We do not wait for a live probe from the host; the record is the signal.
            var hostHasAuth = self.Headers is not null &&
                self.Headers.Keys.Any(k => string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase));
            if (hostHasAuth)
            {
                _enforceBearer = true;
                _listener?.SetEnforceBearer(true);
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            RegistrationMessage = "Could not reconcile with DOCA: " + ex.Message;
            _log.Warn(RegistrationMessage);
            _audit.Add("mcp.reconcile", RegistrationMessage);
            Changed?.Invoke();
        }
    }

    private void EnsureWaitPoll()
    {
        if (_waitPollCts is not null) return;
        var cts = new CancellationTokenSource();
        _waitPollCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(8), cts.Token).ConfigureAwait(false);
                    if (!IsRunning || Registration != McpRegistrationState.WaitingForAccept)
                        break;
                    await ReconcileRegistrationAsync(cts.Token, offerOn404: false).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* stop */ }
            finally
            {
                if (ReferenceEquals(_waitPollCts, cts))
                    _waitPollCts = null;
            }
        });
    }

    private void StopWaitPoll()
    {
        try { _waitPollCts?.Cancel(); } catch { /* ignore */ }
        _waitPollCts = null;
    }

    private async Task OnPushEventAsync(EventEnvelope ev, CancellationToken ct)
    {
        // Devices as hands (§22.1): durable, 24 h TTL, so one may arrive from before this app was
        // last open. DOCA carries out the half it can itself — it closes the stream for reconnect
        // and disconnect — so the ack is the only evidence that *this* side ran the action.
        if (string.Equals(ev.Type, "device.control", StringComparison.OrdinalIgnoreCase))
        {
            DeviceControlPayload? control = null;
            if (ev.Payload is { } ctl)
            {
                try { control = JsonSerializer.Deserialize<DeviceControlPayload>(ctl.GetRawText()); }
                catch { /* an unreadable payload is not worth crashing the push loop for */ }
            }
            // Handed off, not awaited. `refresh` and `reconnect` tear down and reopen the push
            // stream, whose loop is what is calling us — awaited here, the action waited for the
            // loop and the loop waited for the action, and DocaDesk stopped receiving pushes until
            // restarted, then deadlocked again replaying the same unacked event (D-21). The ack
            // still reports the outcome; the loop is free to advance and to be disposed.
            if (control is not null)
                _ = Task.Run(() => _hands.HandleControlAsync(control, CancellationToken.None));
            return;
        }

        if (!string.Equals(ev.Type, "mcp.listener", StringComparison.OrdinalIgnoreCase))
            return;

        McpListenerPayload? payload = null;
        if (ev.Payload is { } el)
        {
            try { payload = JsonSerializer.Deserialize<McpListenerPayload>(el.GetRawText()); }
            catch { /* ignore */ }
        }

        var action = payload?.Action ?? "";
        if (string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase))
        {
            StopWaitPoll();
            await StopListenerOnlyAsync().ConfigureAwait(false);
            // Master switch stays as the user left it — host stop is not an override to "off".
            RegistrationMessage = "Stopped by host request (master switch unchanged).";
            Changed?.Invoke();
            return;
        }

        if (!string.Equals(action, "start", StringComparison.OrdinalIgnoreCase))
            return;

        if (!AppPrefs.McpAutoStart)
        {
            RegistrationMessage = "Host asked to start MCP; refused — listener master switch is off.";
            _audit.Add("mcp.listener", RegistrationMessage);
            Changed?.Invoke();
            return;
        }

        if (!AnyToolConsented())
        {
            RegistrationMessage = "Host asked to start MCP; refused — no tools have consent.";
            _audit.Add("mcp.listener", RegistrationMessage);
            Changed?.Invoke();
            return;
        }

        if (!IsRunning)
        {
            try { await StartAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                RegistrationMessage = "Host start request failed: " + ex.Message;
                Changed?.Invoke();
            }
        }
        else
        {
            // Already running — still reconcile if host URL mismatches.
            await ReconcileRegistrationAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What the person accepting the offer in the dashboard should see: this
    /// machine's own tools plus whatever the local servers currently forward.
    /// </summary>
    private string[] OfferedToolNames() =>
        ToolNames.Concat(_localServers.Tools().Select(t => t.Name)).ToArray();

    private Dictionary<string, string> AuthHeaders() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer " + _bearer,
        };

    private async Task StopListenerOnlyAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            _listener = null;
        }
    }

    private void OnCaptureFlash()
    {
        _audit.Add("capture.indicator", "Tray capture indicator");
        Changed?.Invoke();
    }

    private static string NewBearerToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public async ValueTask DisposeAsync()
    {
        _session.EventReceived -= OnPushEventAsync;
        StopWaitPoll();
        await StopListenerOnlyAsync().ConfigureAwait(false);
        await _sealed.DisarmAsync().ConfigureAwait(false);   // a secret left on the clipboard goes when DocaDesk does
        // These are our child processes; leaving them behind would leak them. Background shell
        // jobs too: after quit nobody could see or stop them.
        ShellTools.StopAllJobs();
        await _localServers.DisposeAsync().ConfigureAwait(false);
    }
}
