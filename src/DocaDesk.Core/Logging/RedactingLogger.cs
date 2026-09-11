using System.Text;
using System.Text.RegularExpressions;

namespace DocaDesk.Core.Logging;

public interface IDocaLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>Redacts tokens, MCP path secrets, and clipboard-like payloads before writing.</summary>
public sealed class RedactingLogger : IDocaLogger
{
    private readonly Action<string> _sink;
    private static readonly Regex Bearer = new(@"Bearer\s+doca_[A-Za-z0-9_.\-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TokenLiteral = new(@"\bdoca_[A-Za-z0-9]+\.[A-Za-z0-9_\-]+\b", RegexOptions.Compiled);
    private static readonly Regex McpPath = new(@"(/mcp/)[A-Za-z0-9_\-]{16,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RedactingLogger(Action<string>? sink = null)
    {
        _sink = sink ?? (m => System.Diagnostics.Debug.WriteLine(m));
    }

    public void Info(string message) => _sink("[INFO] " + Redact(message));
    public void Warn(string message) => _sink("[WARN] " + Redact(message));
    public void Error(string message, Exception? ex = null)
    {
        var sb = new StringBuilder("[ERROR] ").Append(Redact(message));
        if (ex is not null)
            sb.Append(" :: ").Append(Redact(ex.ToString()));
        _sink(sb.ToString());
    }

    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var s = Bearer.Replace(input, "Bearer [REDACTED]");
        s = TokenLiteral.Replace(s, "doca_[REDACTED]");
        s = McpPath.Replace(s, "$1[REDACTED]");

        // Clipboard / secret-looking assignments
        s = Regex.Replace(s, @"(clipboard|password|secret|api[_-]?key)\s*[:=]\s*.+", "$1=[REDACTED]", RegexOptions.IgnoreCase);
        return s;
    }
}
