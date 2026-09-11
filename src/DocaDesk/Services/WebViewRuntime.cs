using Microsoft.Web.WebView2.Core;

namespace DocaDesk.Services;

public static class WebViewRuntime
{
    public static Task<(bool Ok, string? Message)> EnsureInstalledAsync()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrEmpty(version))
            {
                return Task.FromResult<(bool, string?)>((false,
                    "WebView2 Runtime is not installed. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ then restart DocaDesk."));
            }
            return Task.FromResult<(bool, string?)>((true, version));
        }
        catch (Exception ex)
        {
            return Task.FromResult<(bool, string?)>((false,
                $"WebView2 Runtime check failed ({ex.Message}). Install from https://developer.microsoft.com/microsoft-edge/webview2/"));
        }
    }
}
