using DocaDesk.Core.Models;

namespace DocaDesk.Core;

/// <summary>
/// Re-reports this machine's caps when what it is changes — a monitor plugged in or out, the main display's resolution
/// or scale, touch, a battery (PROTOCOL §3.4, §22.1; hub TODO D3, cl 9). Caps are sent on every connect
/// (<c>AppSession.RefreshConnectionAsync</c>, which the <c>refresh</c> action runs too); this catches what changes while
/// connected. A check is a few system calls, so it can run every minute; a report only goes out on a change.
/// </summary>
public sealed class CapsWatcher
{
    private readonly Func<DeviceCaps> _read;
    private readonly Func<DeviceCaps, CancellationToken, Task> _send;
    private string? _last;

    public CapsWatcher(Func<DeviceCaps> read, Func<DeviceCaps, CancellationToken, Task> send)
    {
        _read = read;
        _send = send;
    }

    /// <summary>What the hub reads off caps to shape an answer and choose a machine: the screen, touch, sensors.</summary>
    public static string KeyOf(DeviceCaps c) =>
        $"{c.Screen?.W}x{c.Screen?.H}@{c.Screen?.Dpr}|touch={c.Input?.Touch}|{string.Join(',', c.Sensors ?? [])}";

    /// <summary>Remember what was just sent (on connect), so the next check compares against it.</summary>
    public void Sent(DeviceCaps caps) => _last = KeyOf(caps);

    /// <summary>Send the caps when they differ from the last sent; true when a report went out.</summary>
    public async Task<bool> CheckAsync(CancellationToken ct = default)
    {
        var caps = _read();
        var key = KeyOf(caps);
        if (key == _last) return false;
        await _send(caps, ct).ConfigureAwait(false);
        _last = key;
        return true;
    }
}
