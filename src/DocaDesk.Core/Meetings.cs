using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocaDesk.Core;

/// <summary>A call ringing or about to start (hub meetings, PROTOCOL §23.4): its id, title and who calls.</summary>
public sealed record MeetingRing(string Id, string Title, string By);

/// <summary>A person controlling this machine from a meeting, or no longer (the <c>meeting.control</c> event).</summary>
public sealed record MeetingControl(string Grant, string MeetingId, bool Active, string Controller, string Sharer, string? Why);

/// <summary>
/// Meetings on this desk, every decision pure (MeetingsTests): which alert is a call to join, what the controlled-machine
/// banner says, and what the panel's WebView may be given for a meeting — the camera, the microphone and a screen
/// capture, only on the hub's own address (scheme, host and port), never on another page.
/// </summary>
public static class Meetings
{
    private static readonly Regex IdShape = new("^m[0-9a-f]{12}$", RegexOptions.CultureInvariant);
    private static readonly Regex InText = new(@"/meet/(m[0-9a-f]{12})\b", RegexOptions.CultureInvariant);

    public static bool IsId(string? id) => id is not null && IdShape.IsMatch(id);

    /// <summary>The meeting an alert rings for: its <c>meeting</c> field, or a hub before that, the link in its text.</summary>
    public static MeetingRing? RingOf(JsonElement? payload, string? text = null)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p) return null;
        var title = p.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        if (p.TryGetProperty("meeting", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            var id = Str(m, "id");
            if (IsId(id)) return new MeetingRing(id!, Str(m, "title") ?? "", Str(m, "by") ?? "");
        }
        var found = text is null ? null : InText.Match(text);
        return found is { Success: true } ? new MeetingRing(found.Groups[1].Value, title, "") : null;
    }

    /// <summary>The <c>meeting.control</c> event's payload, or null when it is not one.</summary>
    public static MeetingControl? ControlOf(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p) return null;
        var grant = Str(p, "grant");
        var state = Str(p, "state");
        if (string.IsNullOrEmpty(grant) || state is not ("active" or "ended")) return null;
        return new MeetingControl(grant, Str(p, "meetingId") ?? "", state == "active", Clip(Str(p, "controller") ?? "Someone"), Clip(Str(p, "sharer") ?? ""), Str(p, "why"));
    }

    /// <summary>The banner's words, fixed here: only the controller's name comes from the hub.</summary>
    public static string BannerText(MeetingControl c) => $"{c.Controller} is controlling this computer";

    /// <summary>The room on the hub in use: its address with <c>/meet/&lt;id&gt;</c>, nothing from the notice.</summary>
    public static Uri? RoomUri(Uri serverRoot, string id) =>
        IsId(id) ? new Uri(new Uri(serverRoot.GetLeftPart(UriPartial.Authority) + "/"), "meet/" + id) : null;

    /// <summary>What a WebView permission request is answered: allow the camera and the microphone on the hub, deny them elsewhere, and leave every other kind to WebView2.</summary>
    public static PermissionAnswer Permission(string kind, Uri? origin, Uri serverRoot)
    {
        if (kind is not ("Camera" or "Microphone")) return PermissionAnswer.Default;
        return OnHub(origin, serverRoot) ? PermissionAnswer.Allow : PermissionAnswer.Deny;
    }

    /// <summary>A page asking to capture the screen (getDisplayMedia): the hub's own page may; WebView2's picker then lets the person choose.</summary>
    public static bool ScreenCapture(Uri? origin, Uri serverRoot) => OnHub(origin, serverRoot);

    public static bool OnHub(Uri? u, Uri serverRoot) =>
        u is not null && string.IsNullOrEmpty(u.UserInfo)
        && string.Equals(u.Scheme, serverRoot.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(u.Host, serverRoot.Host, StringComparison.OrdinalIgnoreCase) && u.Port == serverRoot.Port;

    private static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string Clip(string s) => s.Length > 60 ? s[..60] : s;
}

public enum PermissionAnswer { Default, Allow, Deny }
