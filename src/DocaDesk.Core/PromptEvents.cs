namespace DocaDesk.Core;

/// <summary>
/// The hub's prompt events (PROTOCOL §12) as this client acts on them. The hub sends <c>prompt.outcome</c> (an answer's
/// result is ready, or it failed) and <c>prompt.progress</c> (transcribing, thinking); it never sent
/// <c>prompt.updated</c>, which the prompt window listened for alone (hub audit 2026-10-06, cl 23).
/// </summary>
public static class PromptEvents
{
    /// <summary>Whether an open prompt window should show the prompt as it now stands after this event.</summary>
    public static bool IsRefresh(string? type) =>
        string.Equals(type, "prompt.outcome", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "prompt.progress", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "prompt.updated", StringComparison.OrdinalIgnoreCase);   // an older hub's name, harmless

    /// <summary>Whether the prompt is over (answered elsewhere, withdrawn, expired) and its window closes.</summary>
    public static bool IsClosed(string? type) => string.Equals(type, "prompt.closed", StringComparison.OrdinalIgnoreCase);
}
