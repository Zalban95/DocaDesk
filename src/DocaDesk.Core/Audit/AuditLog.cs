using System.Collections.Concurrent;
using System.Text.Json;

namespace DocaDesk.Core.Audit;

public sealed class AuditEntry
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string Kind { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? Session { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Append-only, capped, rotated audit log. Never redacts to uselessness; never logs clipboard contents or image bytes.</summary>
public sealed class AuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();
    private readonly string _path;
    private readonly int _maxEntries;
    private readonly object _fileGate = new();

    public AuditLog(string? path = null, int maxEntries = 500)
    {
        _maxEntries = Math.Max(50, maxEntries);
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocaDesk",
            "audit.jsonl");
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        TryLoad();
    }

    public IReadOnlyList<AuditEntry> Snapshot() => _entries.ToArray();

    public void Add(string kind, string summary, string? session = null, string? detail = null)
    {
        var entry = new AuditEntry
        {
            At = DateTimeOffset.UtcNow,
            Kind = kind,
            Summary = summary,
            Session = session,
            Detail = detail,
        };
        _entries.Enqueue(entry);
        while (_entries.Count > _maxEntries && _entries.TryDequeue(out _)) { }

        lock (_fileGate)
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(entry) + "\n");
            RotateIfNeeded();
        }
    }

    private void TryLoad()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadLines(_path).TakeLast(_maxEntries))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var e = JsonSerializer.Deserialize<AuditEntry>(line);
                if (e is not null) _entries.Enqueue(e);
            }
        }
        catch
        {
            // corrupt log — start fresh in memory
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists || fi.Length < 2_000_000) return;
            var bak = _path + ".1";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(_path, bak);
        }
        catch
        {
            // ignore rotate failures
        }
    }
}
