namespace DocaDesk.Mcp;

/// <summary>
/// The tool families a paired device can offer the harness (PROTOCOL.md §22.1,
/// docs/design/devices-as-hands.md §1).
///
/// **These names are DOCA's, not ours.** `modules/devices-control.js:25` holds the same nine and
/// drops anything it does not recognise from a grants report (`:89`), silently — so a typo here
/// does not fail, it just means the family is never offered to the harness and nobody can see why.
/// Keep the list identical and in the same order.
/// </summary>
public static class ToolFamilies
{
    public const string Files = "files";
    public const string Shell = "shell";
    public const string Processes = "processes";
    public const string Screen = "screen";
    public const string Input = "input";
    public const string Apps = "apps";
    public const string Device = "device";
    public const string Elevated = "elevated";
    public const string Mcp = "mcp";

    /// <summary>All nine, in DOCA's order.</summary>
    public static readonly IReadOnlyList<string> All =
        [Files, Shell, Processes, Screen, Input, Apps, Device, Elevated, Mcp];

    /// <summary>
    /// What this build can actually do on this OS, whatever the person allows. A family absent
    /// here is reported as <c>false</c> rather than omitted, because DOCA distinguishes "refused"
    /// from "not mentioned" only by presence, and a family we cannot serve must never be offered.
    ///
    /// `screen` is listed for Windows although its tools are built in the app (they need
    /// `DocaDesk.Capture`, which this project cannot reference) — a host that constructs no screen
    /// tools simply lists none. `device` and `mcp` are deliberately absent everywhere: see TODO.md.
    /// </summary>
    public static readonly IReadOnlyList<string> Implemented = OperatingSystem.IsWindows()
        ? [Files, Shell, Processes, Screen, Input, Apps, Elevated]
        : [Files, Shell, Processes, Apps];

    /// <summary>A human sentence for the consent prompt. One per family, because the prompt is the consent.</summary>
    public static string Describe(string family) => family switch
    {
        Files => "read, write, move and delete files on this machine",
        Shell => "run commands on this machine",
        Processes => "list, start and stop programs on this machine",
        Screen => "capture this screen and its windows",
        Input => "move the pointer, click and type on this machine",
        Apps => "open apps, links and files on this machine",
        Device => "show notifications and ask you questions",
        Elevated => "run administrator actions (Windows asks for UAC each time)",
        Mcp => "use the MCP servers this machine forwards",
        _ => family,
    };
}
