using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocaDesk.Core.Models;

public sealed class EventEnvelope
{
    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("ts")]
    public string? Ts { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("class")]
    public string? Class { get; set; }

    [JsonPropertyName("ttlSec")]
    public int? TtlSec { get; set; }

    [JsonPropertyName("priority")]
    public string? Priority { get; set; }

    [JsonPropertyName("ack")]
    public bool? Ack { get; set; }

    [JsonPropertyName("v")]
    public int? V { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class HelloPayload
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("cursor")]
    public long? Cursor { get; set; }

    [JsonPropertyName("since")]
    public long? Since { get; set; }

    [JsonPropertyName("replay")]
    public bool? Replay { get; set; }

    [JsonPropertyName("resync")]
    public bool? Resync { get; set; }

    [JsonPropertyName("heartbeatSec")]
    public int? HeartbeatSec { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ClosePayload
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class EventsPollResponse
{
    [JsonPropertyName("events")]
    public List<EventEnvelope>? Events { get; set; }

    [JsonPropertyName("nextSince")]
    public long? NextSince { get; set; }

    [JsonPropertyName("resync")]
    public bool? Resync { get; set; }

    [JsonPropertyName("retryAfterSec")]
    public int? RetryAfterSec { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class AckRequest
{
    [JsonPropertyName("seq")]
    public long Seq { get; set; }
}

public sealed class AckResponse
{
    [JsonPropertyName("acked")]
    public long? Acked { get; set; }

    [JsonPropertyName("pending")]
    public int? Pending { get; set; }

    [JsonPropertyName("cursor")]
    public long? Cursor { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ProfileResponse
{
    [JsonPropertyName("profile")]
    public JsonElement? Profile { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class MediaUploadResponse
{
    [JsonPropertyName("media")]
    public MediaInfo? Media { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class MediaInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("mime")]
    public string? Mime { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("bytes")]
    public long? Bytes { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
