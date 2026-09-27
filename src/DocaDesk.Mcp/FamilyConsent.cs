namespace DocaDesk.Mcp;

/// <summary>
/// What this machine's person has allowed, per family, and what DOCA has taken back.
///
/// Two separate facts, deliberately not merged into one flag:
///
/// * <b>Granted</b> is the person's answer, asked once per family in our own UI and remembered
///   (design §2). Only they change it.
/// * <b>Revoked</b> is DOCA's side saying "stop offering this" (`device.control` `revoke`, undone
///   by `restore`). It must not overwrite the grant — that is the same rule the `mcp.listener`
///   `stop` request already follows: a host request is never permission to rewrite an explicit
///   local choice. A `restore` therefore returns the family to whatever the person had said, which
///   is the only behaviour that cannot silently widen access.
///
/// Storage is two delegates rather than a file or a registry key, because this class has to work
/// unchanged on the Linux client (design §6 step 3) while the WinUI app keeps its prefs in HKCU.
/// Read is expected to return null for "never asked", which is what drives the first-time prompt.
/// </summary>
public sealed class FamilyConsent
{
    private readonly Func<string, bool?> _read;
    private readonly Action<string, bool> _write;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _revoked = new(StringComparer.Ordinal);

    /// <summary>Raised when a grant or a revoke changes, so the report to DOCA and the UI can follow.</summary>
    public event Action? Changed;

    public FamilyConsent(Func<string, bool?> read, Action<string, bool> write)
    {
        _read = read;
        _write = write;
    }

    /// <summary>An in-memory store, for tests and for a first run with nothing persisted.</summary>
    public static FamilyConsent InMemory()
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        return new FamilyConsent(
            f => map.TryGetValue(f, out var v) ? v : null,
            (f, v) => map[f] = v);
    }

    /// <summary>Null when the person has never been asked about this family.</summary>
    public bool? Granted(string family) =>
        ToolFamilies.All.Contains(family, StringComparer.Ordinal) ? _read(family) : false;

    /// <summary>The person's answer. Asking again is how "Ask again" (`device.control ask`) works.</summary>
    public void SetGranted(string family, bool granted)
    {
        if (!ToolFamilies.All.Contains(family, StringComparer.Ordinal)) return;   // DOCA drops unknown names; so do we
        _write(family, granted);
        Changed?.Invoke();
    }

    public bool IsRevoked(string family) => _revoked.TryGetValue(family, out var v) && v;

    /// <summary>DOCA took a family back, or gave it back. The person's grant underneath is untouched.</summary>
    public void SetRevoked(string family, bool revoked)
    {
        if (!ToolFamilies.All.Contains(family, StringComparer.Ordinal)) return;
        _revoked[family] = revoked;
        Changed?.Invoke();
    }

    /// <summary>
    /// The one question every tool asks: may this run right now? Granted here, not revoked there,
    /// and actually implemented in this build.
    /// </summary>
    public bool IsUsable(string family) =>
        ToolFamilies.ImplementedOnWindows.Contains(family, StringComparer.Ordinal)
        && Granted(family) == true
        && !IsRevoked(family);

    /// <summary>
    /// The body of `PUT /devices/self/grants`. Every family appears, because DOCA reads only
    /// `typeof grants[f] === 'boolean'` (`devices-control.js:89`) — omitting one leaves whatever it
    /// held before, and a family we cannot serve must report false rather than go unmentioned.
    /// Revocation is DOCA's own record and is deliberately not echoed back to it.
    /// </summary>
    public Dictionary<string, bool> ReportBody() =>
        ToolFamilies.All.ToDictionary(
            f => f,
            f => ToolFamilies.ImplementedOnWindows.Contains(f, StringComparer.Ordinal) && Granted(f) == true,
            StringComparer.Ordinal);
}
