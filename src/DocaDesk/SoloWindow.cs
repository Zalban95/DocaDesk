using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace DocaDesk;

/// <summary>
/// One of the panel's pages by itself (<c>/?view=workstream</c>, <c>/?view=ambient</c>…) in a window of its own — for a
/// second monitor or a screen given to it. The page's <c>window.open</c> is answered with this window's WebView2, made
/// in the same environment as the main one, so the session cookie comes along and nobody signs in twice. The panel's
/// own full-screen button (or F) fills the screen.
/// </summary>
public sealed class SoloWindow : Window
{
    private readonly WebView2 _web = new();

    private SoloWindow(string page)
    {
        Title = $"DOCA · {page}";
        Content = _web;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
    }

    /// <summary>Open a window for the page a <c>NewWindowRequested</c> asked for and hand its WebView2 to the request.</summary>
    public static async Task OpenAsync(CoreWebView2 opener, CoreWebView2NewWindowRequestedEventArgs e, string page)
    {
        var deferral = e.GetDeferral();
        try
        {
            var w = new SoloWindow(page);
            w.Activate();
            await w._web.EnsureCoreWebView2Async(opener.Environment);
            w._web.CoreWebView2.Settings.UserAgent = opener.Settings.UserAgent;
            w._web.CoreWebView2.ContainsFullScreenElementChanged += (s, _) =>
                w.AppWindow.SetPresenter(s.ContainsFullScreenElement ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
            w._web.CoreWebView2.WindowCloseRequested += (_, _) => w.Close();
            e.NewWindow = w._web.CoreWebView2;
            e.Handled = true;
        }
        finally { deferral.Complete(); }
    }
}
