namespace DocaDesk.Core;

/// <summary>
/// The panel's pages served alone (DOCA 2.227+: <c>/?view=&lt;page&gt;</c>, ⧉ "open alone") open in a DocaDesk window
/// of their own rather than the system browser, where the person is not signed in. Only the configured hub's own
/// pages; anything else still goes to the browser.
/// </summary>
public static class SoloPages
{
    /// <summary>The page name when <paramref name="target"/> is the hub's <c>/?view=…</c> (or <c>/d/&lt;id&gt;/?view=…</c>); else null.</summary>
    public static string? ViewOf(Uri target, Uri serverRoot)
    {
        if (!string.Equals(target.Host, serverRoot.Host, StringComparison.OrdinalIgnoreCase) || target.Port != serverRoot.Port)
            return null;
        if (!string.Equals(target.Scheme, serverRoot.Scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        var path = target.AbsolutePath;
        if (path != "/" && !(path.StartsWith("/d/", StringComparison.Ordinal) && path.EndsWith('/') && path.Count(c => c == '/') == 3))
            return null;
        foreach (var part in target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "view")
            {
                var page = Uri.UnescapeDataString(kv[1]);
                return page.Length is > 0 and <= 40 && page.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? page : null;
            }
        }
        return null;
    }

    /// <summary>What the WebView2 user agent says, so the panel knows it is inside the app (DocaMobile says DocaMobile/x).</summary>
    public static string UserAgentMark => $"{DocaDeskConstants.ClientName}/{DocaDeskConstants.ClientVersion}";
}
