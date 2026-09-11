using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocaDesk.Core.Models;

public sealed class PromptDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("blocks")]
    public List<PromptBlock>? Blocks { get; set; }

    [JsonPropertyName("choices")]
    public List<PromptChoice>? Choices { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("actionAllowed")]
    public bool? ActionAllowed { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class PromptBlock
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Textual degradation for unknown block types (PROTOCOL display field).</summary>
    [JsonPropertyName("display")]
    public string? Display { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class PromptChoice
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>PROTOCOL wire: option|voice|text|image|dismiss. Unknown → treat as option with display.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>Some servers historically used "type"; accept either.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public string EffectiveKind =>
        !string.IsNullOrWhiteSpace(Kind) ? Kind! :
        !string.IsNullOrWhiteSpace(Type) ? Type! :
        "option";
}

public sealed class SelectionRequest
{
    [JsonPropertyName("selectionId")]
    public string SelectionId { get; set; } = "";

    [JsonPropertyName("choiceId")]
    public string ChoiceId { get; set; } = "";

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }
}

public sealed class ConfirmRequest
{
    [JsonPropertyName("selectionId")]
    public string SelectionId { get; set; } = "";

    [JsonPropertyName("decision")]
    public string Decision { get; set; } = "confirm";
}

/// <summary>Generates and tracks selectionIds: same id on retry; new id after back.</summary>
public sealed class SelectionIdTracker
{
    private string? _current;

    public string Current => _current ??= NewId();

    public string Ensure() => Current;

    public string NewId()
    {
        _current = Guid.NewGuid().ToString("D");
        return _current;
    }

    /// <summary>Call after a successful "back" confirm — next select must use a fresh id.</summary>
    public string AfterBack() => NewId();

    public void Reset() => _current = null;
}
