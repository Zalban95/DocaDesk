using System.IO;
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
    private static readonly Regex Bearer = new(@"Bearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TokenLiteral = new(@"\bdoca_[A-Za-z0-9]+\.[A-Za-z0-9_\-]+\b", RegexOptions.Compiled);
    private static readonly Regex McpPath = new(@"(/mcp/)[A-Za-z0-9_\-]{16,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Where a log line goes when the caller does not say.
    ///
    /// It used to go only to <c>Debug.WriteLine</c>, which carries
    /// <c>[Conditional("DEBUG")]</c> — the compiler deletes the call from a
    /// Release build. Every construction site takes this default, so the shipped
    /// app logged nothing at all, anywhere. That is not a missing nicety: the SSE
    /// reader was failing on every connection and writing "SSE failed, falling
    /// back to JSON poll" each time, correctly, into a call that no longer
    /// existed. The bug was findable in one line of log for as long as it has
    /// been there.
    ///
    /// The file is opened per line and every failure is swallowed: logging must
    /// never be the reason something breaks, and two threads appending short
    /// lines is what <see cref="File.AppendAllText"/> is for.
    /// </summary>
    public RedactingLogger(Action<string>? sink = null)
    {
        _sink = sink ?? DefaultSink;
    }

    /// <summary>The shipped destination: the debugger when attached, and a file always.</summary>
    private static void DefaultSink(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocaDesk");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "docadesk.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // A log line is never worth an exception on the path that produced it.
        }
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
