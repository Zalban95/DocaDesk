namespace DocaDesk.Core.Push;

public interface ICursorStore
{
    Task<long> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(long seq, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class FileCursorStore : ICursorStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileCursorStore(string? path = null)
    {
        _path = path
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DocaDesk",
                "event_cursor.txt");
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public async Task<long> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return 0;
            var text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            return long.TryParse(text.Trim(), out var seq) ? seq : 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(long seq, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(_path, seq.ToString(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class MemoryCursorStore : ICursorStore
{
    private long _seq;

    public Task<long> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_seq);

    public Task SaveAsync(long seq, CancellationToken cancellationToken = default)
    {
        // Gaps are allowed: only advance, never retreat, unless Clear.
        if (seq > _seq)
            _seq = seq;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _seq = 0;
        return Task.CompletedTask;
    }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? start = null) => UtcNow = start ?? DateTimeOffset.UnixEpoch;

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

/// <summary>
/// Heartbeat watchdog: no traffic for 2 × heartbeatSec (default 50s when heartbeat is 25) triggers reconnect.
/// Uses an injectable clock so tests do not wait wall-clock time.
/// </summary>
public sealed class HeartbeatWatchdog
{
    private readonly IClock _clock;
    private DateTimeOffset _lastTraffic;
    private TimeSpan _timeout;

    public HeartbeatWatchdog(IClock clock, TimeSpan? timeout = null)
    {
        _clock = clock;
        _timeout = timeout ?? TimeSpan.FromSeconds(50);
        _lastTraffic = clock.UtcNow;
    }

    public void Configure(int heartbeatSec)
    {
        var sec = heartbeatSec > 0 ? heartbeatSec * 2 : 50;
        _timeout = TimeSpan.FromSeconds(sec);
    }

    public void MarkTraffic() => _lastTraffic = _clock.UtcNow;

    public bool IsExpired => _clock.UtcNow - _lastTraffic >= _timeout;

    public TimeSpan Timeout => _timeout;
}
