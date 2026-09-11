using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocaDesk.Core.Models;

/// <summary>Device capabilities declared at pair / reported honestly from the machine.</summary>
public sealed class DeviceCaps
{
    [JsonPropertyName("formFactor")]
    public string? FormFactor { get; set; }

    [JsonPropertyName("screen")]
    public ScreenCaps? Screen { get; set; }

    [JsonPropertyName("input")]
    public InputCaps? Input { get; set; }

    [JsonPropertyName("render")]
    public List<string>? Render { get; set; }

    [JsonPropertyName("motion")]
    public List<string>? Motion { get; set; }

    [JsonPropertyName("exec")]
    public List<string>? Exec { get; set; }

    [JsonPropertyName("sensors")]
    public List<string>? Sensors { get; set; }

    /// <summary>Unknown / future fields preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ScreenCaps
{
    [JsonPropertyName("w")]
    public int? W { get; set; }

    [JsonPropertyName("h")]
    public int? H { get; set; }

    [JsonPropertyName("dpr")]
    public double? Dpr { get; set; }

    [JsonPropertyName("shape")]
    public string? Shape { get; set; }

    [JsonPropertyName("color")]
    public bool? Color { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class InputCaps
{
    [JsonPropertyName("touch")]
    public bool? Touch { get; set; }

    [JsonPropertyName("voice")]
    public bool? Voice { get; set; }

    [JsonPropertyName("text")]
    public bool? Text { get; set; }

    [JsonPropertyName("camera")]
    public bool? Camera { get; set; }

    [JsonPropertyName("buttons")]
    public bool? Buttons { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class PairCompleteRequest
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("caps")]
    public DeviceCaps? Caps { get; set; }
}

public sealed class PairCompleteResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("device")]
    public JsonElement? Device { get; set; }

    [JsonPropertyName("capabilitiesUrl")]
    public string? CapabilitiesUrl { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Loose capabilities document — consume fields at runtime; never hard-code ids from Appendix A.</summary>
public sealed class CapabilitiesDocument
{
    [JsonPropertyName("protocol")]
    public JsonElement? Protocol { get; set; }

    [JsonPropertyName("push")]
    public PushCapabilities? Push { get; set; }

    [JsonPropertyName("limits")]
    public JsonElement? Limits { get; set; }

    [JsonPropertyName("media")]
    public MediaCapabilities? Media { get; set; }

    [JsonPropertyName("scopes")]
    public JsonElement? Scopes { get; set; }

    [JsonPropertyName("device")]
    public JsonElement? Device { get; set; }

    [JsonPropertyName("surfaces")]
    public JsonElement? Surfaces { get; set; }

    [JsonPropertyName("commands")]
    public JsonElement? Commands { get; set; }

    [JsonPropertyName("prompts")]
    public JsonElement? Prompts { get; set; }

    [JsonPropertyName("deprecations")]
    public JsonElement? Deprecations { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class PushCapabilities
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("ackUrl")]
    public string? AckUrl { get; set; }

    [JsonPropertyName("heartbeatSec")]
    public int? HeartbeatSec { get; set; }

    [JsonPropertyName("cursor")]
    public long? Cursor { get; set; }

    [JsonPropertyName("pending")]
    public int? Pending { get; set; }

    [JsonPropertyName("backoff")]
    public BackoffCapabilities? Backoff { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class BackoffCapabilities
{
    [JsonPropertyName("initialMs")]
    public int? InitialMs { get; set; }

    [JsonPropertyName("maxMs")]
    public int? MaxMs { get; set; }

    [JsonPropertyName("factor")]
    public double? Factor { get; set; }

    [JsonPropertyName("jitter")]
    public double? Jitter { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class MediaCapabilities
{
    [JsonPropertyName("uploadUrl")]
    public string? UploadUrl { get; set; }

    [JsonPropertyName("accept")]
    public List<string>? Accept { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
