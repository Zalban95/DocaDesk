using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace DocaDesk.Services;

/// <summary>
/// Hosts the Doca dashboard. No script injection, no JS bridge (M2).
///
/// One exception to M2's "no Authorization header", made once DOCA had sign-in
/// (DOCA 2.56.0+, auth phase 1): the <b>first</b> load of the server root carries the
/// device token, and DOCA answers with its own session cookie — the way DocaMobile's
/// WebView signs in — so a paired desk does not ask for the password as well. Only
/// that navigation, only to the configured server; every later request rides the
/// cookie DOCA set, and the token is never attached to anything else. ISSUES.md D-9.
/// </summary>
public sealed class DashboardHost
{
    private readonly WebView2 _webView;
    private Uri? _serverRoot;
    private bool _hooked;

    public DashboardHost(WebView2 webView) => _webView = webView;

    public event Action<string>? NavigationFailed;
    public event Action? NavigationSucceeded;

    public async Task InitializeAsync(Uri serverRoot)
    {
        _serverRoot = new Uri(serverRoot.GetLeftPart(UriPartial.Authority) + "/");
        var (ok, msg) = await WebViewRuntime.EnsureInstalledAsync();
        if (!ok)
            throw new InvalidOperationException(msg);

        await _webView.EnsureCoreWebView2Async();
        if (!_hooked)
        {
            _hooked = true;
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            // No WebMessageReceived bridge. The one Authorization header is NavigateHome's first load (D-9).
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            NavigationSucceeded?.Invoke();
            return;
        }

        var url = _serverRoot?.ToString() ?? "(unknown)";
        NavigationFailed?.Invoke(
            $"Cannot load dashboard at {url.TrimEnd('/')}. Check the URL and network, then Retry.");
    }

    private string? _deviceToken;
    private bool _signedIn;

    /// <summary>The device token for the first load of the server root (D-9). Null: sign in by password.</summary>
    public void UseDeviceToken(string? token) { _deviceToken = string.IsNullOrEmpty(token) ? null : token; _signedIn = false; }

    public void NavigateHome()
    {
        if (_serverRoot is null || _webView.CoreWebView2 is null)
            return;
        if (_deviceToken is not null && !_signedIn)
        {
            _signedIn = true;   // once: DOCA's cookie carries every request after this one
            var req = _webView.CoreWebView2.Environment.CreateWebResourceRequest(
                _serverRoot.ToString(), "GET", null, $"Authorization: Bearer {_deviceToken}");
            _webView.CoreWebView2.NavigateWithWebResourceRequest(req);
            return;
        }
        _webView.CoreWebView2.Navigate(_serverRoot.ToString());
    }

    public void Reload() => _webView.CoreWebView2?.Reload();

    private double _zoom = 1.0;

    public void ZoomIn() => SetZoom(_zoom + 0.1);

    public void ZoomOut() => SetZoom(_zoom - 0.1);

    public void ZoomReset() => SetZoom(1.0);

    private void SetZoom(double factor)
    {
        _zoom = Math.Clamp(factor, 0.5, 3.0);
        // WinUI WebView2 has no ZoomFactor property; CSS zoom is one-way and not a JS bridge.
        if (_webView.CoreWebView2 is not null)
        {
            var pct = (_zoom * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            _ = _webView.ExecuteScriptAsync($"document.documentElement.style.zoom = '{pct}%';");
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_serverRoot is null) return;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            return;

        if (IsSameHost(uri, _serverRoot))
            return;

        e.Cancel = true;
        _ = Launcher.LaunchUriAsync(uri);
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            _ = Launcher.LaunchUriAsync(uri);
    }

    private static bool IsSameHost(Uri a, Uri b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
}
