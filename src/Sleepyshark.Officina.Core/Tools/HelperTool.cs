using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// <c>builtin:team.start_helper</c>: the agent starts a helper agent for a piece of its work, and gets its output (TEAM-07).
/// Only an agent whose definition lists the helper may, within the team's depth and count limits; the helper's budget and
/// permissions come out of the agent's. It declares itself a read: whatever the helper does goes through its own tools, gates
/// and approvals, and is audited there.
/// </summary>
internal sealed class HelperTool : ITool
{
    public const string Name = "team.start_helper";

    public static HelperTool Instance { get; } = new();

    public ToolDescriptor Descriptor { get; } = new(
        "Starts a helper agent for a piece of your work, such as finding something out, and returns what it did. Name the helper by its definition, one you may start, and give it all it needs: it sees nothing else of your work.",
        JsonDocument.Parse("""{ "type": "object", "properties": { "agent": { "type": "string", "minLength": 1 }, "work": { "type": "string", "minLength": 1 } }, "required": ["agent", "work"], "additionalProperties": false }""")
            .RootElement.Clone(),
        ToolKind.Read);

    public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
    {
        if (toolCall.StartHelper is null)
        {
            return ToolResult.Failed(ToolErrorCategory.InvalidArguments, "this agent cannot start helpers here.");
        }

        var (completed, text) = await toolCall.StartHelper(
            toolCall.Arguments.GetProperty("agent").GetString()!, toolCall.Arguments.GetProperty("work").GetString()!, ct).ConfigureAwait(false);
        return completed ? ToolResult.Success(text) : ToolResult.Failed(ToolErrorCategory.Failed, text);
    }
}
