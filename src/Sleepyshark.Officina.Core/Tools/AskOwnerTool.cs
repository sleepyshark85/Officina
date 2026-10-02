using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// <c>builtin:human.ask_owner</c>: the agent asks the owner a question and continues the same turn with the answer
/// (HITL-06). The tool pipeline asks, after the usual steps, because only it knows the run and the agent asking.
/// </summary>
internal sealed class AskOwnerTool : ITool
{
    /// <summary>The tool's name in <c>builtin:</c> sources.</summary>
    public const string Name = "human.ask_owner";

    public static AskOwnerTool Instance { get; } = new();

    public ToolDescriptor Descriptor { get; } = new(
        "Asks the owner a question, and returns the answer. Ask only what you cannot find out or decide yourself.",
        JsonDocument.Parse("""{ "type": "object", "properties": { "question": { "type": "string" } }, "required": ["question"], "additionalProperties": false }""")
            .RootElement.Clone(),
        ToolKind.Read);

    public ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct) =>
        throw new NotSupportedException($"The tool pipeline asks the owner for {Name}.");

    public static string Question(JsonElement arguments) => arguments.GetProperty("question").GetString()!;
}
