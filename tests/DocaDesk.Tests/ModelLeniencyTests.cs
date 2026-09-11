using DocaDesk.Core;
using DocaDesk.Core.Models;

namespace DocaDesk.Tests;

public class ModelLeniencyTests
{
    [Fact]
    public void Unknown_fields_are_ignored_without_throwing()
    {
        const string json = """
            {
              "formFactor": "desktop",
              "futureField": 42,
              "screen": { "w": 1920, "h": 1080, "extra": true },
              "nested": { "a": 1 }
            }
            """;

        var caps = DocaJson.Deserialize<DeviceCaps>(json);
        Assert.NotNull(caps);
        Assert.Equal("desktop", caps!.FormFactor);
        Assert.Equal(1920, caps.Screen!.W);
        Assert.NotNull(caps.ExtensionData);
        Assert.True(caps.ExtensionData!.ContainsKey("futureField"));
    }

    [Fact]
    public void Unknown_event_type_deserializes()
    {
        const string json = """
            { "seq": 9, "type": "totally.new.event", "payload": { "x": 1 }, "mystery": true }
            """;
        var ev = DocaJson.Deserialize<EventEnvelope>(json);
        Assert.NotNull(ev);
        Assert.Equal(9, ev!.Seq);
        Assert.Equal("totally.new.event", ev.Type);
    }

    [Fact]
    public void Prompt_block_unknown_type_keeps_display()
    {
        const string json = """
            { "type": "hologram", "display": "see hologram", "text": "ignored for unknown" }
            """;
        var block = DocaJson.Deserialize<PromptBlock>(json);
        Assert.Equal("hologram", block!.Type);
        Assert.Equal("see hologram", block.Display);
    }
}
