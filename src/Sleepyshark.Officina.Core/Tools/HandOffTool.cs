using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// <c>builtin:human.request_handoff</c>: the agent hands its work to a human, saying why (EGR-04). It is the explicit signal a
/// model gives; words in its text never are. The turn ends in a handoff to a human.
/// </summary>
internal sealed class HandOffTool : ITool
{
    public const string Name = "human.request_handoff";

    public static HandOffTool Instance { get; } = new();

    public ToolDescriptor Descriptor { get; } = new(
        "Hands your work to a human, when you cannot or should not go on without one, and says why. Your turn ends.",
        JsonDocument.Parse("""{ "type": "object", "properties": { "reason": { "type": "string", "minLength": 1 } }, "required": ["reason"], "additionalProperties": false }""")
            .RootElement.Clone(),
        ToolKind.Read);

    public ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
    {
        var reason = toolCall.Arguments.GetProperty("reason").GetString()!;
        return ValueTask.FromResult(ToolResult.Success("Handed to a human.") with { HandOffToHuman = reason });
    }
}
