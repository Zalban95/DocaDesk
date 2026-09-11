using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace DocaDesk.Services;

/// <summary>
/// Hosts the Doca dashboard. No Authorization header, no script injection, no JS bridge (M2).
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
            // Explicitly do NOT add Authorization headers or WebMessageReceived bridge.
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

    public void NavigateHome()
    {
        if (_serverRoot is null || _webView.CoreWebView2 is null)
            return;
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
