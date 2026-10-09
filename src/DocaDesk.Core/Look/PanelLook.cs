using System.Text.Json;
using System.Text.Json.Serialization;
using DocaDesk.Core.Models;

namespace DocaDesk.Core.Look;

/// <summary>
/// The panel's look, resolved by the hub for this device: <c>GET /api/v1/settings/look</c> (PROTOCOL §14.1, hub
/// <c>device-look</c>, 2.342.0). Colours by role, the ground, the style, its font lists and corners — so the app's own
/// windows can be drawn the way the panel is. Unknown fields are kept out; a role the hub adds later is simply unused.
/// </summary>
public sealed class PanelLook
{
    [JsonPropertyName("theme")] public string? Theme { get; set; }
    [JsonPropertyName("themeLabel")] public string? ThemeLabel { get; set; }
    [JsonPropertyName("skin")] public string? Skin { get; set; }
    [JsonPropertyName("skinLabel")] public string? SkinLabel { get; set; }
    /// <summary><c>light</c> or <c>dark</c>, by the ground's luminance, as the panel decides it.</summary>
    [JsonPropertyName("ground")] public string? Ground { get; set; }
    /// <summary>Hex colours by role: bg, surface, raised, border, text, muted, accent, onAccent, red, …</summary>
    [JsonPropertyName("palette")] public Dictionary<string, string>? Palette { get; set; }
    [JsonPropertyName("fonts")] public LookFonts? Fonts { get; set; }
    [JsonPropertyName("radius")] public LookRadius? Radius { get; set; }
    [JsonPropertyName("inputSize")] public double? InputSize { get; set; }
    [JsonPropertyName("etag")] public string? Etag { get; set; }
}

public sealed class LookFonts
{
    [JsonPropertyName("ui")] public List<string>? Ui { get; set; }
    [JsonPropertyName("text")] public List<string>? Text { get; set; }
    [JsonPropertyName("display")] public List<string>? Display { get; set; }
    [JsonPropertyName("mono")] public List<string>? Mono { get; set; }
}

public sealed class LookRadius
{
    [JsonPropertyName("control")] public double? Control { get; set; }
    [JsonPropertyName("card")] public double? Card { get; set; }
}

public sealed class LookResponse
{
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("look")] public PanelLook? Look { get; set; }
}

public static class PanelLookWire
{
    /// <summary>The look in a <c>GET /settings/look</c> body, or null when the body is not one.</summary>
    public static PanelLook? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return DocaJson.Deserialize<LookResponse>(json)?.Look; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Whether a push event says the look changed: <c>settings.changed</c> with <c>look: true</c> (a change on this
    /// device's layer or its person's that touches theme, customTheme or skin).
    /// </summary>
    public static bool IsLookChange(EventEnvelope? ev)
    {
        if (ev?.Type != "settings.changed" || ev.Payload is not { ValueKind: JsonValueKind.Object } p) return false;
        return p.TryGetProperty("look", out var look) && look.ValueKind == JsonValueKind.True;
    }
}
