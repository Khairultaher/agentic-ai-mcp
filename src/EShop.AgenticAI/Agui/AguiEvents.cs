using System.Text.Json;
using System.Text.Json.Serialization;

namespace EShop.AgenticAI.Agui;

// Minimal subset of the AG-UI (Agent ↔ User Interaction) protocol event types.
// Spec: https://docs.ag-ui.com — events are SSE frames carrying JSON like
//   data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"...","delta":"..."}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RunStartedEvent),         "RUN_STARTED")]
[JsonDerivedType(typeof(RunFinishedEvent),        "RUN_FINISHED")]
[JsonDerivedType(typeof(TextMessageStartEvent),   "TEXT_MESSAGE_START")]
[JsonDerivedType(typeof(TextMessageContentEvent), "TEXT_MESSAGE_CONTENT")]
[JsonDerivedType(typeof(TextMessageEndEvent),     "TEXT_MESSAGE_END")]
[JsonDerivedType(typeof(CustomEvent),             "CUSTOM")]
[JsonDerivedType(typeof(StateSnapshotEvent),      "STATE_SNAPSHOT")]
internal abstract record AguiEvent;

internal sealed record RunStartedEvent(string ThreadId, string RunId) : AguiEvent;
internal sealed record RunFinishedEvent(string ThreadId, string RunId) : AguiEvent;
internal sealed record TextMessageStartEvent(string MessageId, string Role = "assistant") : AguiEvent;
internal sealed record TextMessageContentEvent(string MessageId, string Delta) : AguiEvent;
internal sealed record TextMessageEndEvent(string MessageId) : AguiEvent;
internal sealed record CustomEvent(string Name, JsonElement Value) : AguiEvent;
internal sealed record StateSnapshotEvent(JsonElement Snapshot) : AguiEvent;

internal static class AguiSerializer
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
