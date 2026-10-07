using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace DocaDesk.Services;

/// <summary>
/// Hosts the Doca dashboard. No script injection, no JS bridge (M2). Its pages served alone open in a
/// <see cref="SoloWindow"/>; the user agent ends in <c>DocaDesk/&lt;version&gt;</c>.
///
/// One exception to M2's "no Authorization header", made once DOCA had sign-in
/// (DOCA 2.56.0+, auth phase 1): the <b>first</b> load of the server root carries the
/// device token, and DOCA answers with its own session cookie — the way DocaMobile's
/// WebView signs in — so a paired desk does not ask for the password as well. Only
/// that navigation, only to the configured server; every later request rides the
/// cookie DOCA set, and the token is never attached to anything else. ISSUES.md D-9.
/// </summary>
public sealed class DashboardHost : DocaDesk.Mcp.ISecretField
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
            // Who is asking: the panel keeps the "get the app" banner away and knows there is a second window to open.
            var ua = _webView.CoreWebView2.Settings.UserAgent;
            if (!ua.Contains(DocaDesk.Core.SoloPages.UserAgentMark, StringComparison.Ordinal))
                _webView.CoreWebView2.Settings.UserAgent = $"{ua} {DocaDesk.Core.SoloPages.UserAgentMark}";
            // No WebMessageReceived bridge. The one Authorization header is NavigateHome's first load (D-9).
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            // Only a load that arrived spends the token: DOCA has answered with its session
            // cookie, so every later request rides that instead. Marking it spent before the
            // attempt meant one unreachable server (Tailscale still coming up, DOCA restarting)
            // burnt it for the life of the process — and D-9's symptom came straight back (D-10).
            _signedIn = true;
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
            // _signedIn is set in OnNavigationCompleted, on success only (D-10).
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
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) { e.Handled = true; return; }
        // The hub's own page served alone (⧉): a DocaDesk window sharing this session. Anything else: the browser.
        var page = _serverRoot is null ? null : DocaDesk.Core.SoloPages.ViewOf(uri, _serverRoot);
        if (page is not null) { _ = SoloWindow.OpenAsync(sender, e, page); return; }
        e.Handled = true;
        _ = Launcher.LaunchUriAsync(uri);
    }

    /* ── Sealed secrets: a credential field of the page this window shows (PROTOCOL.md §22.3) ── */

    /// <summary>
    /// Fill a password (or one-time-code) field of the page the panel window shows — only when that page's origin is
    /// exactly <paramref name="origin"/>, checked here and again inside the page, where it cannot change underneath.
    /// <paramref name="reference"/> counts the page's credential fields in document order from 1; 0 means the focused
    /// one, else the only one. The value reaches the page as a JSON-encoded argument, never spliced into script text,
    /// and is set the way clients/browser/page.js fillSecret does: the native setter, then input and change.
    /// </summary>
    public Task FillAsync(string origin, int? tab, int reference, string value, CancellationToken ct)
    {
        if (tab is not null and not 0)
            throw new DocaDesk.Mcp.SecretRefusedException("DocaDesk fills only the page its panel window shows: leave tab out.");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_webView.DispatcherQueue.TryEnqueue(async () =>
            {
                try { await FillOnUiAsync(origin, reference, value); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
            throw new DocaDesk.Mcp.SecretRefusedException("DocaDesk's panel window is closing: try again.");
        return tcs.Task;
    }

    private async Task FillOnUiAsync(string origin, int reference, string value)
    {
        var core = _webView.CoreWebView2
            ?? throw new DocaDesk.Mcp.SecretRefusedException("DocaDesk's panel has no page loaded yet.");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var want) || !Uri.TryCreate(core.Source, UriKind.Absolute, out var shown)
            || !string.Equals(OriginOf(want), OriginOf(shown), StringComparison.OrdinalIgnoreCase))
            throw new DocaDesk.Mcp.SecretRefusedException($"The page DocaDesk shows is not on {origin}, so the secret was not filled.");

        const string Script = """
            ((origin, ref, value) => {
              if (location.origin !== new URL(origin).origin) return { error: 'origin' };
              const ac = el => String(el.getAttribute('autocomplete') || '').toLowerCase();
              const takes = el => !!el && el.tagName === 'INPUT'
                && (String(el.type || '').toLowerCase() === 'password' || /(current-password|new-password|one-time-code)/.test(ac(el)));
              const all = Array.from(document.querySelectorAll('input')).filter(takes);
              const el = ref > 0 ? all[ref - 1] : (takes(document.activeElement) ? document.activeElement : (all.length === 1 ? all[0] : null));
              if (!el) return { error: 'ref', count: all.length };
              el.focus();
              const d = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value');
              if (d && d.set) d.set.call(el, value); else el.value = value;
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
              return { ok: true };
            })
            """;
        var call = $"{Script}({System.Text.Json.JsonSerializer.Serialize(origin)}, {reference}, {System.Text.Json.JsonSerializer.Serialize(value)})";
        string result;
        try { result = await core.ExecuteScriptAsync(call); }
        catch (Exception ex) { throw new DocaDesk.Mcp.SecretRefusedException($"The page would not run the fill ({ex.GetType().Name})."); }
        finally { call = null; }

        using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(result) ? "null" : result);
        var r = doc.RootElement;
        if (r.ValueKind == System.Text.Json.JsonValueKind.Object && r.TryGetProperty("ok", out _)) return;
        var why = r.ValueKind == System.Text.Json.JsonValueKind.Object && r.TryGetProperty("error", out var e) ? e.GetString() : null;
        var count = r.ValueKind == System.Text.Json.JsonValueKind.Object && r.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : 0;
        throw new DocaDesk.Mcp.SecretRefusedException(why switch
        {
            "origin" => $"The page DocaDesk shows is not on {origin}, so the secret was not filled.",
            "ref" when reference > 0 => $"There is no password field [{reference}] on the page: it has {count} (numbered from 1 in page order).",
            "ref" => count == 0
                ? "The page DocaDesk shows has no password field (or one marked for a password or a one-time code), so a secret does not go into it."
                : $"The page has {count} password fields and none has the focus: give ref 1 to {count}.",
            _ => "The page did not take the secret.",
        });
    }

    private static string OriginOf(Uri u) => $"{u.Scheme}://{u.Host}:{u.Port}";

    private static bool IsSameHost(Uri a, Uri b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
}
