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
    private McpHttpListener? _listener;
    private string _secret = McpHttpListener.NewPathSecret();
    private string _bearer = NewBearerToken();
    private bool _enforceBearer;
    private CancellationTokenSource? _waitPollCts;

    public McpHost(AppSession session, ICredentialStore? creds = null, IDocaLogger? log = null)
    {
        _session = session;
        _creds = creds ?? (OperatingSystem.IsWindows() ? new DpapiCredentialStore() : new MemoryCredentialStore());
        _log = log ?? new RedactingLogger();
        _localServers = new LocalMcpRegistry(LocalMcpRegistry.DefaultStorePath(), _consent, _audit);
        _localServers.ToolsChanged += () => Changed?.Invoke();
        _session.EventReceived += OnPushEventAsync;
    }

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
    }

    public void SetConsent(string tool, bool enabled)
    {
        _consent.Set(tool, enabled);
        AppPrefs.SetToolConsent(tool, enabled);
    }

    /// <summary>
    /// A consented local MCP server counts: a machine may exist to forward one
    /// filesystem server and never consent to a screenshot.
    /// </summary>
    public bool AnyToolConsented() => ToolNames.Any(t => _consent.IsEnabled(t)) || _localServers.Tools().Count > 0;

    public async Task StartAsync()
    {
        await StopListenerOnlyAsync().ConfigureAwait(false);
        var host = new Uri(_session.ServerUrl).Host;
        var tools = DeskTools.Create(_capturer, () => _session.Client, _audit, OnCaptureFlash);
        _listener = new McpHttpListener(new McpListenerOptions
        {
            PathSecret = _secret,
            AllowedRemoteHost = host,
            Consent = _consent,
            Audit = _audit,
            Tools = tools,
            // Asked per request, so a local server that stops also stops being offered.
            DynamicTools = _localServers.Tools,
            RequiredBearerToken = _bearer,
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

            var hostUrl = self.Url ?? "";
            var hostHasAuth = self.Headers is not null &&
                self.Headers.Keys.Any(k => string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase));

            if (!string.Equals(hostUrl, url, StringComparison.Ordinal) || !hostHasAuth)
            {
                self = await client.PatchMcpSelfAsync(new McpSelfPatchRequest
                {
                    Url = url,
                    Headers = headers,
                }, ct).ConfigureAwait(false);
                _log.Info("MCP self address/headers patched");
                _audit.Add("mcp.patch", "Updated host entry for this device");
            }

            Registration = McpRegistrationState.Registered;
            RegistrationMessage = $"Registered on host as {self.Id ?? self.Label ?? "mcp server"}.";

            // Safe to enforce once the host record carries Authorization (§3.4).
            // We do not wait for a live probe from the host; the record is the signal.
            hostHasAuth = self.Headers is not null &&
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
        // These are our child processes; leaving them behind would leak them.
        await _localServers.DisposeAsync().ConfigureAwait(false);
    }
}
