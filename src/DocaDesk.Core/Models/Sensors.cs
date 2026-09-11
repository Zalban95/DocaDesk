using System.Text.Json.Serialization;

namespace DocaDesk.Core.Models;

public sealed class SensorSample
{
    [JsonPropertyName("sensor")]
    public required string Sensor { get; init; }

    [JsonPropertyName("value")]
    public required double Value { get; init; }

    [JsonPropertyName("t")]
    public long? T { get; init; }
}
