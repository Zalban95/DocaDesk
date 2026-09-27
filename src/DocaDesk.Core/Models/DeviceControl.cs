using System.Text.Json.Serialization;

namespace DocaDesk.Core.Models;

/// <summary>
/// The `device.control` push payload (PROTOCOL.md §11.4): what DOCA is asking this device to do.
/// Durable with a 24 h TTL, so one may well arrive from before the app was last closed.
/// </summary>
public sealed class DeviceControlPayload
{
    /// <summary>`dc_…`. Echo it back to `POST /devices/self/control/{id}/ack` or the action stays unanswered for ever.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>refresh | reconnect | ask | disconnect | revoke | restore.</summary>
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    /// <summary>Set for `ask`, `revoke` and `restore`; DOCA rejects those three without it (devices-control.js:49).</summary>
    [JsonPropertyName("family")]
    public string? Family { get; set; }
}

/// <summary>`POST /api/v1/devices/self/control/{id}/ack`. `detail` is truncated to 300 chars server-side.</summary>
public sealed class DeviceControlAckRequest
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

/// <summary>
/// `PUT /api/v1/devices/self/grants`. Every family is sent explicitly: DOCA keeps only the keys
/// whose value is a boolean (devices-control.js:89), so an omitted family keeps whatever it held.
/// </summary>
public sealed class DeviceGrantsRequest
{
    [JsonPropertyName("grants")]
    public Dictionary<string, bool> Grants { get; set; } = new();
}

/// <summary>What DOCA reports back: the grants as recorded, and which families it will actually offer.</summary>
public sealed class DeviceGrantsResponse
{
    [JsonPropertyName("grants")]
    public Dictionary<string, bool>? Grants { get; set; }

    /// <summary>Granted here and not revoked there. This is the list the harness is offered.</summary>
    [JsonPropertyName("usable")]
    public List<string>? Usable { get; set; }

    [JsonPropertyName("revoked")]
    public List<string>? Revoked { get; set; }
}
