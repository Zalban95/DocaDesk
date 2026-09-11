using DocaDesk.Capture;
using DocaDesk.Core.Audit;
using DocaDesk.Core.Security;
using DocaDesk.Mcp;

namespace DocaDesk.Services;

/// <summary>Owns MCP listener lifecycle, secret, consent, and audit.</summary>
public sealed class McpHost : IAsyncDisposable
{
    private readonly AppSession _session;
    private readonly ICredentialStore _creds;
    private readonly AuditLog _audit = new();
    private readonly ScreenCapturer _capturer = new();
    private readonly ToolConsent _consent = new();
    private McpHttpListener? _listener;
    private string _secret = McpHttpListener.NewPathSecret();

    public McpHost(AppSession session, ICredentialStore? creds = null)
    {
        _session = session;
        _creds = creds ?? (OperatingSystem.IsWindows() ? new DpapiCredentialStore() : new MemoryCredentialStore());
    }

    public AuditLog Audit => _audit;
    public ToolConsent Consent => _consent;
    public bool IsRunning => _listener?.IsRunning == true;
    public string? Url => _listener?.BoundUrl;
    public string? LastError => _listener?.LastError;
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        var existing = await _creds.LoadAsync(CredentialKeys.McpPathSecret);
        if (!string.IsNullOrEmpty(existing))
            _secret = existing;
        else
            await _creds.SaveAsync(CredentialKeys.McpPathSecret, _secret);

        foreach (var tool in ToolNames)
            _consent.Set(tool, AppPrefs.GetToolConsent(tool));
    }

    public static readonly string[] ToolNames =
    [
        "list_windows", "screenshot", "get_clipboard_text", "set_clipboard_text", "open_url",
    ];

    public void SetConsent(string tool, bool enabled)
    {
        _consent.Set(tool, enabled);
        AppPrefs.SetToolConsent(tool, enabled);
    }

    public async Task StartAsync()
    {
        await StopAsync();
        var host = new Uri(_session.ServerUrl).Host;
        var tools = DeskTools.Create(_capturer, () => _session.Client, _audit, OnCaptureFlash);
        _listener = new McpHttpListener(new McpListenerOptions
        {
            PathSecret = _secret,
            AllowedRemoteHost = host,
            Consent = _consent,
            Audit = _audit,
            Tools = tools,
        });
        try
        {
            await _listener.StartAsync();
        }
        catch
        {
            Changed?.Invoke();
            throw;
        }
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync();
            _listener = null;
        }
        Changed?.Invoke();
    }

    public async Task RegenerateSecretAsync()
    {
        var was = IsRunning;
        await StopAsync();
        _secret = McpHttpListener.NewPathSecret();
        await _creds.SaveAsync(CredentialKeys.McpPathSecret, _secret);
        if (was)
            await StartAsync();
        else
            Changed?.Invoke();
    }

    private void OnCaptureFlash()
    {
        // Tray tooltip flash — MainWindow / TrayHost can subscribe via Changed + Audit.
        _audit.Add("capture.indicator", "Tray capture indicator");
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
