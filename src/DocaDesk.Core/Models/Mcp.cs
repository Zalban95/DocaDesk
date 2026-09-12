using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocaDesk.Core.Models;

public sealed class McpSelfResponse
{
    [JsonPropertyName("server")]
    public McpServerView? Server { get; set; }
}

public sealed class McpServerView
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("autostart")]
    public bool? Autostart { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("toolCount")]
    public int? ToolCount { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class McpOfferRequest
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<string>? Tools { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public sealed class McpOfferResponse
{
    [JsonPropertyName("offer")]
    public McpOfferView? Offer { get; set; }
}

public sealed class McpOfferView
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class McpSelfPatchRequest
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Ignored by the server (§22 / addendum). Included only in tests that assert that property.</summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    [JsonPropertyName("autostart")]
    public bool? Autostart { get; set; }
}

public sealed class McpListenerPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("serverId")]
    public string? ServerId { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("by")]
    public string? By { get; set; }
}
