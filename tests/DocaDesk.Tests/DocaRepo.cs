namespace DocaDesk.Tests;

/// <summary>
/// Finding the sibling DOCA checkout, so the two tests that drive DOCA's own
/// <c>modules/mcp/client.js</c> against this listener keep running when the repo moves.
///
/// They used to hardcode <c>D:\doca\doca\DOCA\modules\mcp\client.js</c>. DOCA's root became
/// <c>D:\doca\doca</c>, the path stopped existing, and because the guard is a bare
/// <c>return</c> both tests reported *passed* in two milliseconds for weeks — including
/// across the D-8 handshake change they exist to check (ISSUES.md D-11).
///
/// So: no literal. <c>DOCA_REPO</c> wins if it is set, otherwise walk up from the test
/// binary and try both layouts at every level. A wrong answer is still possible, but a
/// *silent* one now needs the file to be missing everywhere, which is the honest case.
/// </summary>
internal static class DocaRepo
{
    private const string Rel = "modules/mcp/client.js";

    /// <summary>Full path to DOCA's MCP client, or null when no checkout is reachable.</summary>
    public static string? ClientJs { get; } = Locate();

    private static string? Locate()
    {
        var env = Environment.GetEnvironmentVariable("DOCA_REPO");
        if (!string.IsNullOrWhiteSpace(env))
        {
            // Point it at either the repo or its parent; both spellings are what people type.
            foreach (var c in new[] { Path.Combine(env, Rel), Path.Combine(env, "doca", Rel) })
                if (File.Exists(c)) return c;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            // "doca/DOCA" is the old layout — kept so an older machine is not disarmed either.
            foreach (var rel in new[] { Path.Combine("doca", Rel), Path.Combine("doca", "DOCA", Rel) })
            {
                var candidate = Path.Combine(dir.FullName, rel);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
