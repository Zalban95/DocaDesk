using System.Text.Json.Nodes;

namespace DocaDesk.Mcp;

/// <summary>
/// A tool that belongs to a family (§22.1): listed only while its family is usable, and refused if
/// it is called anyway — the second gate covers the race between a list and a call.
///
/// Listing only usable families is the device making design §2 true on its own: *"a tool the device
/// would refuse is not shown as available"*. DOCA's `modules/mcp/tools.js` exposes every tool a
/// device's server lists, whatever its grants (ISSUES.md D-17), so without this the harness would
/// see — and waste steps on — a family nobody allowed.
///
/// It can also rename a tool, which is how the `screen` family reuses the existing
/// `list_windows`/`screenshot` implementations under family names without a second capture path.
/// </summary>
public sealed class FamilyTool : IMcpTool
{
    private readonly IMcpTool _inner;
    private readonly FamilyConsent _consent;
    private readonly string? _name;
    private readonly string? _description;

    public FamilyTool(string family, IMcpTool inner, FamilyConsent consent, string? name = null, string? description = null)
    {
        Family = family;
        _inner = inner;
        _consent = consent;
        _name = name;
        _description = description;
    }

    public string Family { get; }
    public string Name => _name ?? _inner.Name;
    public string Description => _description ?? _inner.Description;
    public bool ReadOnlyHint => _inner.ReadOnlyHint;
    public JsonObject InputSchema => _inner.InputSchema;

    public Task<McpToolResult> CallAsync(JsonNode? args, string sessionId, CancellationToken ct) =>
        _consent.IsUsable(Family)
            ? _inner.CallAsync(args, sessionId, ct)
            : Task.FromResult(new McpToolResult
            {
                IsError = true,
                Text = _consent.IsRevoked(Family)
                    ? $"The {Family} family on this machine was revoked in DOCA."
                    : $"The {Family} family is not allowed on this machine. Turn it on in DocaDesk → Settings → This device.",
            });

    /// <summary>The tools of every family that is usable right now. Asked on every list and call.</summary>
    public static IMcpTool[] Offered(IEnumerable<FamilyTool> tools, FamilyConsent consent) =>
        tools.Where(t => consent.IsUsable(t.Family)).ToArray<IMcpTool>();
}
