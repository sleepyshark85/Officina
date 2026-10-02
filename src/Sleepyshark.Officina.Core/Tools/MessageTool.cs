using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// <c>builtin:team.message</c>: an agent of a team sends another agent of the same team and run a message (TEAM-05, TEAM-06).
/// It names its sender and recipient, is recorded as an event, and reaches the recipient as data labelled with its sender,
/// before its next model call (CTX-08, INV-08). Like the task tools it declares itself a read: its only effect is on the
/// team's own agents, recorded, never outside the core, so it needs no gate.
/// </summary>
internal sealed class MessageTool : ITool
{
    public const string Name = "team.message";

    public static MessageTool Instance { get; } = new();

    public ToolDescriptor Descriptor { get; } = new(
        "Sends another agent of your team a message, such as a question about its task or something it needs to know. Name it by its id in the team, such as developer[2]; your work tells you who is in the team.",
        JsonDocument.Parse("""{ "type": "object", "properties": { "to": { "type": "string", "minLength": 1 }, "text": { "type": "string", "minLength": 1 } }, "required": ["to", "text"], "additionalProperties": false }""")
            .RootElement.Clone(),
        ToolKind.Read,
        ParallelSafe: true);

    public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
    {
        var to = toolCall.Arguments.GetProperty("to").GetString()!;
        var problem = toolCall.Send is null
            ? "only the agents of a team send each other messages."
            : await toolCall.Send(to, toolCall.Arguments.GetProperty("text").GetString()!, ct).ConfigureAwait(false);
        return problem is null ? ToolResult.Success($"Sent to {to}.") : ToolResult.Failed(ToolErrorCategory.InvalidArguments, problem);
    }
}
