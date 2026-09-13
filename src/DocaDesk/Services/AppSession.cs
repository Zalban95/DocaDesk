using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DocaDesk.Core;
using DocaDesk.Core.Logging;
using DocaDesk.Core.Models;
using DocaDesk.Core.Net;
using DocaDesk.Core.Push;
using DocaDesk.Core.Security;

namespace DocaDesk.Services;

public enum SessionState
{
    Unpaired,
    Pairing,
    Paired,
    Offline,
    Revoked,
}

/// <summary>Owns token, server URL, cert pin, and the live <see cref="DocaClient"/>.</summary>
public sealed class AppSession : IAsyncDisposable
{
    public const string DefaultServerUrl = "https://al-office-desk.tail08f157.ts.net:4242/";

    private readonly ICredentialStore _creds;
    private readonly IDocaLogger _log;
    private DocaClient? _client;
    private PushEngine? _push;
    private readonly ICursorStore _cursor = new FileCursorStore();
    private CancellationTokenSource? _batteryCts;

    public AppSession(ICredentialStore? creds = null, IDocaLogger? log = null)
    {
        _creds = creds ?? (OperatingSystem.IsWindows()
            ? new DpapiCredentialStore()
            : new MemoryCredentialStore());
        _log = log ?? new RedactingLogger();
    }

    public SessionState State { get; private set; } = SessionState.Unpaired;
    public string? LastError { get; private set; }
    public string ServerUrl { get; private set; } = DefaultServerUrl;
    public DocaClient? Client => _client;
    public CapabilitiesDocument? Capabilities { get; private set; }
    public PushEngine? Push => _push;

    /// <summary>Raised for every push event (unknown types included).</summary>
    public event Func<EventEnvelope, CancellationToken, Task>? EventReceived;

    /// <summary>Fired when the device token is wiped due to revoke/401. UI should stop MCP.</summary>
    public event Func<CancellationToken, Task>? Revoked;

    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ServerUrl = await _creds.LoadAsync(CredentialKeys.ServerUrl, ct).ConfigureAwait(false)
            ?? DefaultServerUrl;
        var token = await _creds.LoadAsync(CredentialKeys.DeviceToken, ct).ConfigureAwait(false);
        var pin = await _creds.LoadAsync(CredentialKeys.CertPinSha256, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(token))
        {
            State = SessionState.Unpaired;
            Changed?.Invoke();
            return;
        }

        await RecreateClientAsync(token, pin, ct).ConfigureAwait(false);
        await RefreshConnectionAsync(ct).ConfigureAwait(false);
    }

    public async Task SetServerUrlAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Server URL must be http(s).");

        ServerUrl = uri.ToString().TrimEnd('/') + "/";
        await _creds.SaveAsync(CredentialKeys.ServerUrl, ServerUrl, ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>
    /// Pair with optional explicit pin. If the server is self-signed and unpinned,
    /// <paramref name="acceptPin"/> can accept the observed fingerprint.
    /// </summary>
    public async Task PairAsync(string code, string deviceName, string? acceptPin = null, CancellationToken ct = default)
    {
        State = SessionState.Pairing;
        LastError = null;
        Changed?.Invoke();

        var pin = acceptPin ?? await _creds.LoadAsync(CredentialKeys.CertPinSha256, ct).ConfigureAwait(false);
        await RecreateClientAsync(token: null, pin, ct).ConfigureAwait(false);

        try
        {
            var caps = DeviceCapsFactory.FromMachine();
            var result = await _client!.PairCompleteAsync(new PairCompleteRequest
            {
                Code = code.Trim(),
                Name = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim(),
                Caps = caps,
            }, ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(result.Token))
                throw new InvalidPairingException("Pairing returned no token");

            await _creds.SaveAsync(CredentialKeys.DeviceToken, result.Token, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(acceptPin))
                await _creds.SaveAsync(CredentialKeys.CertPinSha256, acceptPin, ct).ConfigureAwait(false);

            await RecreateClientAsync(result.Token, acceptPin ?? pin, ct).ConfigureAwait(false);
            await RefreshConnectionAsync(ct).ConfigureAwait(false);
        }
        catch (CertificateException cex)
        {
            LastError = cex.Message;
            State = SessionState.Unpaired;
            Changed?.Invoke();
            throw;
        }
        catch (Exception ex) when (ex is not DocaException)
        {
            LastError = ex.Message;
            State = SessionState.Unpaired;
            Changed?.Invoke();
            throw new NetworkException(ex.Message, ex);
        }
        catch (DocaException dex)
        {
            LastError = dex.Message;
            State = SessionState.Unpaired;
            Changed?.Invoke();
            throw;
        }
    }

    /// <summary>
    /// Forget the local device token and stop push/MCP. Protocol forbids self-revoke
    /// (<c>DELETE /devices/:id</c> while authenticated as that device); revoke from the dashboard
    /// or another admin device. Local unpair still leaves the server row until then.
    /// </summary>
    public async Task UnpairAsync(CancellationToken ct = default)
    {
        StopBatteryReporter();
        await StopPushAsync().ConfigureAwait(false);
        await _creds.DeleteAsync(CredentialKeys.DeviceToken, ct).ConfigureAwait(false);
        // Keep server URL and optional pin so re-pair is easy.
        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);
        _client = null;
        Capabilities = null;
        State = SessionState.Unpaired;
        LastError = null;
        Changed?.Invoke();
    }

    public async Task RefreshConnectionAsync(CancellationToken ct = default)
    {
        if (_client is null || string.IsNullOrEmpty(_client.Token))
        {
            StopBatteryReporter();
            State = SessionState.Unpaired;
            Changed?.Invoke();
            return;
        }

        try
        {
            Capabilities = await _client.GetCapabilitiesAsync(ct).ConfigureAwait(false);
            State = SessionState.Paired;
            LastError = null;
            await EnsurePushAsync(ct).ConfigureAwait(false);
            StartBatteryReporter();
        }
        catch (InvalidTokenException)
        {
            await HandleRevokedAsync(ct).ConfigureAwait(false);
        }
        catch (UnauthenticatedException)
        {
            await HandleRevokedAsync(ct).ConfigureAwait(false);
        }
        catch (CertificateException cex)
        {
            StopBatteryReporter();
            await StopPushAsync().ConfigureAwait(false);
            State = SessionState.Offline;
            LastError = cex.Message;
        }
        catch (Exception ex)
        {
            StopBatteryReporter();
            await StopPushAsync().ConfigureAwait(false);
            State = SessionState.Offline;
            LastError = $"Cannot reach {ServerUrl.TrimEnd('/')}: {ex.Message}";
            _log.Warn(LastError);
        }

        Changed?.Invoke();
    }

    private async Task EnsurePushAsync(CancellationToken ct)
    {
        if (_client is null) return;
        await StopPushAsync().ConfigureAwait(false);
        _push = new PushEngine(new PushEngineOptions
        {
            Client = _client,
            CursorStore = _cursor,
            Logger = _log,
            // Await every subscriber, not just the last one.
            //
            // Invoking a multicast delegate runs all the handlers but hands back
            // only the final one's Task. There are two — McpHost subscribes
            // first, PromptCoordinator second — so McpHost's work was
            // fire-and-forget: an mcp.listener "stop" could be acked and the
            // cursor advanced while the listener was still accepting requests,
            // and anything it threw vanished into an unobserved task with no log
            // line. Sequential rather than WhenAll on purpose: these handlers
            // touch UI state and the order they were registered in is the order
            // they have always run in.
            OnEvent = async (ev, token) =>
            {
                var handler = EventReceived;
                if (handler is null) return;
                foreach (var one in handler.GetInvocationList())
                    await ((Func<EventEnvelope, CancellationToken, Task>)one)(ev, token).ConfigureAwait(false);
            },
            OnResync = async (_, token) =>
            {
                try
                {
                    Capabilities = await _client.GetCapabilitiesAsync(token).ConfigureAwait(false);
                    // Full resync: drop profile etag so next read is fresh; push cursor is server-owned.
                    await _client.GetProfileAsync(etag: null, ct: token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn("Resync refetch failed: " + ex.Message);
                }
            },
            OnRevoked = HandleRevokedAsync,
        });
        _push.ConfigureFromCapabilities(Capabilities);
        _push.Start();
    }

    private async Task StopPushAsync()
    {
        if (_push is null) return;
        await _push.DisposeAsync().ConfigureAwait(false);
        _push = null;
    }

    private async Task HandleRevokedAsync(CancellationToken ct)
    {
        StopBatteryReporter();
        await StopPushAsync().ConfigureAwait(false);
        await _creds.DeleteAsync(CredentialKeys.DeviceToken, ct).ConfigureAwait(false);
        State = SessionState.Revoked;
        LastError = "This device was revoked on the server. Pair again.";
        Capabilities = null;
        _log.Warn("Token revoked");
        if (Revoked is not null)
            await Revoked(ct).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private void StartBatteryReporter()
    {
        StopBatteryReporter();
        if (!OperatingSystem.IsWindows() || !DeviceCapsFactory.TryReadBatteryPercent(out _))
            return;
        _batteryCts = new CancellationTokenSource();
        var linked = _batteryCts.Token;
        _ = Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    var client = _client;
                    if (client is not null && DeviceCapsFactory.TryReadBatteryPercent(out var pct))
                    {
                        await client.PostSensorSamplesAsync(
                            [new SensorSample { Sensor = "battery", Value = pct }],
                            ct: linked).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Warn("Battery sample failed: " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), linked).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, linked);
    }

    private void StopBatteryReporter()
    {
        try { _batteryCts?.Cancel(); } catch { /* ignore */ }
        _batteryCts?.Dispose();
        _batteryCts = null;
    }
    /// <summary>Probe TLS fingerprint without accepting the connection (for pin UI).</summary>
    public async Task<string?> ProbeCertificateFingerprintAsync(CancellationToken ct = default)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    // Accept temporarily only to read the cert — caller decides whether to pin.
                    return cert is not null;
                },
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(ServerUrl), "/api/v1/"));
        try
        {
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch
        {
            // still may have seen cert via callback — fall through
        }

        // Better: connect via SslStream
        var uri = new Uri(ServerUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;

        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port, ct).ConfigureAwait(false);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = uri.Host,
        }, ct).ConfigureAwait(false);
        var cert = ssl.RemoteCertificate as X509Certificate2 ?? new X509Certificate2(ssl.RemoteCertificate!);
        return CertificatePolicy.FingerprintSha256(cert);
    }

    private async Task RecreateClientAsync(string? token, string? pin, CancellationToken ct)
    {
        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);

        _client = new DocaClient(new DocaClientOptions
        {
            BaseAddress = new Uri(ServerUrl),
            Token = token,
            ClientName = DocaDeskConstants.ClientName,
            ClientVersion = DocaDeskConstants.ClientVersion,
            CertificatePolicy = new CertificatePolicy(pin),
            Logger = _log,
        });
    }

    public async ValueTask DisposeAsync()
    {
        StopBatteryReporter();
        await StopPushAsync().ConfigureAwait(false);
        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);
    }
}
