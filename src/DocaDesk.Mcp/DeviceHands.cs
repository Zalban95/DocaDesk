using DocaDesk.Core.Audit;
using DocaDesk.Core.Models;
using DocaDesk.Core.Net;

namespace DocaDesk.Mcp;

/// <summary>
/// This device as one of the harness's hands: reporting what its person has allowed, and carrying
/// out the actions DOCA sends (PROTOCOL.md §22.1, docs/design/devices-as-hands.md).
///
/// Deliberately here rather than in <c>src\DocaDesk</c>, and deliberately free of WinUI: the Linux
/// client is meant to reuse this file unchanged (design §6 step 3). Everything that needs a window
/// — asking the person, dropping the push stream — arrives as a delegate the host app supplies.
///
/// <b>Every action is acked, including the ones that fail.</b> DOCA keeps each action in a 20-entry
/// history with `ackAt`, `ok` and `detail` (`devices-control.js:57,80`), and that history is what a
/// person looks at in Settings → Devices to find out whether the machine heard them. An action that
/// threw and said nothing is indistinguishable there from a client that is not listening — which is
/// the state this whole class exists to end.
/// </summary>
public sealed class DeviceHands
{
    private readonly Func<DocaClient?> _client;
    private readonly FamilyConsent _consent;
    private readonly AuditLog? _audit;

    public DeviceHands(Func<DocaClient?> client, FamilyConsent consent, AuditLog? audit = null)
    {
        _client = client;
        _consent = consent;
        _audit = audit;
        // Owned here, not by the host app, so the Linux client inherits the right wiring: report
        // when the person's answer changes, and never because DOCA's revocations were written back
        // by a report — that loop is D-19.
        _consent.GrantsChanged += () => _ = ReportGrantsAsync();
    }

    /// <summary>Drop and reopen the push stream (`reconnect`). DOCA closes its end too, 300 ms later.</summary>
    public Func<CancellationToken, Task>? OnReconnect { get; set; }

    /// <summary>
    /// Close sessions and stop background work until the person opens the app again (`disconnect`).
    /// <b>Stay paired</b> — the token is not forgotten; that is `revoked`, a different event.
    /// </summary>
    public Func<CancellationToken, Task>? OnDisconnect { get; set; }

    /// <summary>Report caps again (`refresh`). Grants are reported by this class regardless.</summary>
    public Func<CancellationToken, Task>? OnRefreshCaps { get; set; }

    /// <summary>
    /// Ask the person about one family, again (`ask`). Returns their answer, or null when it could
    /// not be put to them — which is acked as a failure rather than guessed at in either direction.
    /// </summary>
    public Func<string, CancellationToken, Task<bool?>>? AskForFamily { get; set; }

    /// <summary>The last thing DOCA said it would offer the harness, for the UI to show.</summary>
    public IReadOnlyList<string> Usable { get; private set; } = [];

    /// <summary>Raised after a report or an action changes something the window draws.</summary>
    public event Action? Changed;

    /// <summary>
    /// Tell DOCA what this machine allows. Safe to call whenever it might have changed — on
    /// connect, after a consent toggle, on `refresh` — and it also clears a disconnect on DOCA's
    /// side, so it is how the device says it is back.
    /// </summary>
    public async Task<bool> ReportGrantsAsync(CancellationToken ct = default)
    {
        var client = _client();
        if (client is null) return false;
        try
        {
            var body = new DeviceGrantsRequest { Grants = _consent.ReportBody() };
            var res = await client.PutDeviceGrantsAsync(body, ct).ConfigureAwait(false);
            Usable = res.Usable ?? [];

            // DOCA is the authority on what it has taken back, and it tells us on every report —
            // so this is also how a revoke made while the app was closed reaches us.
            foreach (var f in ToolFamilies.All)
                _consent.SetRevoked(f, res.Revoked?.Contains(f) == true);

            _audit?.Add("device.grants", string.Join(", ", Usable.Count > 0 ? Usable : ["(none)"]));
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _audit?.Add("device.grants.fail", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Carry out one `device.control` action and answer it. Never throws: a failure becomes
    /// `ok: false` with the reason, because an unanswered action is the worse outcome.
    /// </summary>
    public async Task HandleControlAsync(DeviceControlPayload p, CancellationToken ct = default)
    {
        var id = p.Id;
        var action = (p.Action ?? "").Trim();
        if (string.IsNullOrEmpty(id))
        {
            _audit?.Add("device.control.bad", $"{action}: no id to acknowledge");
            return;
        }

        var ok = true;
        string detail;
        try
        {
            detail = await RunAsync(action, p.Family, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ok = false;
            detail = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }

        _audit?.Add("device.control", $"{action}{(p.Family is null ? "" : " " + p.Family)}: {detail}");
        Changed?.Invoke();

        try
        {
            // Not cancelled with ct: a disconnect or reconnect is exactly the action whose own
            // token is about to be torn down, and an ack that never arrives is the bug above.
            var client = _client();
            if (client is not null)
                await client.AckDeviceControlAsync(id, new DeviceControlAckRequest { Ok = ok, Detail = detail }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _audit?.Add("device.control.ack.fail", $"{action}: {ex.Message}");
        }
    }

    private async Task<string> RunAsync(string action, string? family, CancellationToken ct)
    {
        switch (action)
        {
            case "refresh":
                if (OnRefreshCaps is not null) await OnRefreshCaps(ct).ConfigureAwait(false);
                await ReportGrantsAsync(ct).ConfigureAwait(false);
                return $"caps and grants reported; usable: {Describe(Usable)}";

            case "reconnect":
                if (OnReconnect is null) return "no push stream to reopen";
                await OnReconnect(ct).ConfigureAwait(false);
                return "push stream reopened";

            case "disconnect":
                if (OnDisconnect is null) return "nothing running to stop";
                await OnDisconnect(ct).ConfigureAwait(false);
                return "sessions closed; still paired";

            case "ask":
                {
                    var f = Require(family, action);
                    if (AskForFamily is null) return $"cannot ask for {f}: no window";
                    var answer = await AskForFamily(f, ct).ConfigureAwait(false);
                    if (answer is null) return $"{f} was not put to the person";
                    _consent.SetGranted(f, answer.Value);   // reported through GrantsChanged
                    return $"{f} {(answer.Value ? "allowed" : "refused")}";
                }

            // revoke/restore are DOCA's own record of what it will offer. They must not rewrite the
            // person's grant underneath — a restore returns the family to whatever they had said,
            // which is the only reading that cannot silently widen access.
            case "revoke":
            case "restore":
                {
                    var f = Require(family, action);
                    _consent.SetRevoked(f, action == "revoke");
                    return $"{f} {(action == "revoke" ? "no longer offered" : "offered again")}"
                        + (action == "restore" && _consent.Granted(f) != true ? " (still not allowed on this machine)" : "");
                }

            default:
                // Forward-compatible: a newer DOCA may send an action this build predates, and
                // saying so is more useful than a silent success or a silent nothing.
                return $"this device does not know the action '{action}'";
        }
    }

    private static string Require(string? family, string action) =>
        string.IsNullOrWhiteSpace(family)
            ? throw new ArgumentException($"{action} needs a family.")
            : family.Trim();

    private static string Describe(IReadOnlyList<string> families) =>
        families.Count == 0 ? "(none)" : string.Join(", ", families);
}
