using System.Net;
using System.Text;
using DocaDesk.Core.Logging;
using DocaDesk.Core.Models;
using DocaDesk.Core.Net;

namespace DocaDesk.Core.Push;

public enum PushMode
{
    Sse,
    JsonPoll,
}

public sealed class PushEngineOptions
{
    public required DocaClient Client { get; init; }
    public required ICursorStore CursorStore { get; init; }
    public IClock Clock { get; init; } = new SystemClock();
    public IDocaLogger? Logger { get; init; }
    public Func<EventEnvelope, CancellationToken, Task>? OnEvent { get; init; }
    public Func<HelloPayload, CancellationToken, Task>? OnHello { get; init; }
    public Func<string?, CancellationToken, Task>? OnResync { get; init; }
    public Func<string?, CancellationToken, Task>? OnClose { get; init; }
    public Func<CancellationToken, Task>? OnRevoked { get; init; }
}

/// <summary>
/// SSE push with 2×heartbeat watchdog, capabilities backoff, JSON-poll fallback on the same cursor.
/// </summary>
public sealed class PushEngine : IAsyncDisposable
{
    private readonly PushEngineOptions _opt;
    private readonly IDocaLogger _log;
    private readonly HeartbeatWatchdog _watchdog;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private PushCapabilities? _pushCaps;

    public PushEngine(PushEngineOptions options)
    {
        _opt = options;
        _log = options.Logger ?? new RedactingLogger();
        _watchdog = new HeartbeatWatchdog(options.Clock);
    }

    public PushMode Mode { get; private set; } = PushMode.Sse;
    public bool IsRunning => _loop is { IsCompleted: false };

    public void ConfigureFromCapabilities(CapabilitiesDocument? caps)
    {
        _pushCaps = caps?.Push;
        var hb = _pushCaps?.HeartbeatSec ?? 25;
        _watchdog.Configure(hb);
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(_pushCaps?.Backoff?.InitialMs ?? 1000);
        var max = TimeSpan.FromMilliseconds(_pushCaps?.Backoff?.MaxMs ?? 60_000);
        var factor = _pushCaps?.Backoff?.Factor ?? 2.0;
        var jitter = _pushCaps?.Backoff?.Jitter ?? 0.2;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Mode = PushMode.Sse;
                await RunSseAsync(ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(_pushCaps?.Backoff?.InitialMs ?? 1000);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (InvalidTokenException)
            {
                if (_opt.OnRevoked is not null)
                    await _opt.OnRevoked(ct).ConfigureAwait(false);
                break;
            }
            catch (UnauthenticatedException)
            {
                if (_opt.OnRevoked is not null)
                    await _opt.OnRevoked(ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                _log.Warn("SSE failed, falling back to JSON poll: " + ex.Message);
                try
                {
                    Mode = PushMode.JsonPoll;
                    await RunPollOnceAsync(ct).ConfigureAwait(false);
                    delay = TimeSpan.FromMilliseconds(_pushCaps?.Backoff?.InitialMs ?? 1000);
                }
                catch (InvalidTokenException)
                {
                    if (_opt.OnRevoked is not null)
                        await _opt.OnRevoked(ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception pollEx)
                {
                    _log.Warn("JSON poll failed: " + pollEx.Message);
                }
            }

            var sleep = ApplyJitter(delay, jitter);
            await Task.Delay(sleep, ct).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(max.TotalMilliseconds, delay.TotalMilliseconds * factor));
        }
    }

    private async Task RunSseAsync(CancellationToken ct)
    {
        var since = await _opt.CursorStore.LoadAsync(ct).ConfigureAwait(false);
        using var res = await _opt.Client.OpenEventStreamAsync(since, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidTokenException();
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw DocaErrorMapper.FromHttp((int)res.StatusCode, body);
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var parser = new SseParser();
        _watchdog.MarkTraffic();

        while (!ct.IsCancellationRequested)
        {
            var readTask = reader.ReadLineAsync(ct).AsTask();
            var delayTask = Task.Delay(TimeSpan.FromSeconds(1), ct);
            var finished = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
            if (_watchdog.IsExpired)
                throw new TimeoutException("Push heartbeat watchdog expired");

            if (finished != readTask)
                continue;

            var line = await readTask.ConfigureAwait(false);
            if (line is null)
                throw new EndOfStreamException("SSE stream ended");

            _watchdog.MarkTraffic();
            foreach (var frame in parser.PushLine(line))
                await HandleFrameAsync(frame, ct).ConfigureAwait(false);
        }
    }

    private async Task RunPollOnceAsync(CancellationToken ct)
    {
        var since = await _opt.CursorStore.LoadAsync(ct).ConfigureAwait(false);
        var poll = await _opt.Client.PollEventsAsync(since, ct).ConfigureAwait(false);
        if (poll.Resync == true && _opt.OnResync is not null)
            await _opt.OnResync("poll", ct).ConfigureAwait(false);

        if (poll.Events is not null)
        {
            foreach (var ev in poll.Events.OrderBy(e => e.Seq))
                await DispatchEventAsync(ev, ct).ConfigureAwait(false);
        }

        if (poll.NextSince is long next)
            await _opt.CursorStore.SaveAsync(next, ct).ConfigureAwait(false);
    }

    private async Task HandleFrameAsync(SseParser.Frame frame, CancellationToken ct)
    {
        if (frame.Event == ":comment")
            return; // heartbeat

        if (string.Equals(frame.Event, "hello", StringComparison.OrdinalIgnoreCase))
        {
            var hello = DocaJson.Deserialize<HelloPayload>(frame.Data) ?? new HelloPayload();
            if (hello.HeartbeatSec is int hb)
                _watchdog.Configure(hb);
            if (hello.Resync == true && _opt.OnResync is not null)
                await _opt.OnResync("hello", ct).ConfigureAwait(false);
            if (_opt.OnHello is not null)
                await _opt.OnHello(hello, ct).ConfigureAwait(false);
            if (hello.Cursor is long c)
                await _opt.CursorStore.SaveAsync(c, ct).ConfigureAwait(false);
            return;
        }

        if (string.Equals(frame.Event, "close", StringComparison.OrdinalIgnoreCase))
        {
            var close = DocaJson.Deserialize<ClosePayload>(frame.Data);
            if (string.Equals(close?.Reason, "revoked", StringComparison.OrdinalIgnoreCase))
            {
                if (_opt.OnRevoked is not null)
                    await _opt.OnRevoked(ct).ConfigureAwait(false);
            }
            if (_opt.OnClose is not null)
                await _opt.OnClose(close?.Reason, ct).ConfigureAwait(false);
            throw new EndOfStreamException("SSE close: " + close?.Reason);
        }

        // Default / named events carry EventEnvelope JSON in data
        if (string.IsNullOrWhiteSpace(frame.Data))
            return;

        EventEnvelope? envelope;
        try
        {
            envelope = DocaJson.Deserialize<EventEnvelope>(frame.Data);
        }
        catch
        {
            _log.Warn("Ignoring undecodable SSE data frame");
            return;
        }

        if (envelope is null)
            return;

        // Unknown event types: still advance cursor / ack if durable
        await DispatchEventAsync(envelope, ct).ConfigureAwait(false);
    }

    private async Task DispatchEventAsync(EventEnvelope envelope, CancellationToken ct)
    {
        if (_opt.OnEvent is not null)
            await _opt.OnEvent(envelope, ct).ConfigureAwait(false);

        // Gaps allowed: always store the seq we processed
        await _opt.CursorStore.SaveAsync(envelope.Seq, ct).ConfigureAwait(false);

        if (envelope.Ack == true)
        {
            try
            {
                await _opt.Client.AckAsync(envelope.Seq, _pushCaps?.AckUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("Ack failed: " + ex.Message);
            }
        }
    }

    private static TimeSpan ApplyJitter(TimeSpan delay, double jitter)
    {
        if (jitter <= 0) return delay;
        var rand = Random.Shared.NextDouble() * 2 - 1; // [-1,1]
        var ms = delay.TotalMilliseconds * (1 + jitter * rand);
        return TimeSpan.FromMilliseconds(Math.Max(100, ms));
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
