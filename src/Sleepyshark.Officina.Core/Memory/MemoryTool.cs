using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Memory;

/// <summary>
/// A built-in memory tool, through which agents propose changes to project memory and the lead decides on them
/// (MEM-03). Like the record tools, it declares itself a read: it changes only memory's log, which checks every change
/// and keeps it with its author, so it needs no gate. Where the owner approves, the tool pipeline asks the owner
/// before a proposal is made, and the proposal is then applied at once.
/// </summary>
internal sealed class MemoryTool : ITool
{
    public const string Propose = "memory.propose_change";
    public const string Review = "memory.review";

    private const string Text = """{ "type": "string", "minLength": 1 }""";

    private readonly Func<ToolCall, CancellationToken, Task<(bool Accepted, string Text)>> run;

    private MemoryTool(string description, string schema, Func<ToolCall, CancellationToken, Task<(bool, string)>> run)
    {
        Descriptor = new(description, JsonDocument.Parse(schema).RootElement.Clone(), ToolKind.Read, ParallelSafe: true);
        this.run = run;
    }

    /// <summary>The memory tools, by the name <c>builtin:</c> sources use.</summary>
    public static IReadOnlyDictionary<string, MemoryTool> All { get; } = new Dictionary<string, MemoryTool>
    {
        [Propose] = new(
            "Proposes a change to project memory, which every agent sees from its next conversation on and the others are told of: a note (an instruction, a convention, how to build and test, a summary of the architecture) or a decision that affects the whole project, with its reason. To replace entries, give their numbers; replacing several with one condenses memory.",
            $$"""
            { "type": "object", "properties": { "kind": { "enum": ["note", "decision"] }, "subject": {{Text}}, "text": {{Text}}, "reason": {{Text}},
              "replaces": { "type": "array", "items": { "type": "integer" } } }, "required": ["kind", "subject", "text"], "additionalProperties": false }
            """, ProposeAsync),
        [Review] = new(
            "Approves or rejects a proposed change to project memory, and says why. You cannot decide on your own proposal, and the owner reviews condensing.",
            $$"""{ "type": "object", "properties": { "id": { "type": "integer" }, "approved": { "type": "boolean" }, "reason": {{Text}} }, "required": ["id", "approved", "reason"], "additionalProperties": false }""",
            ReviewAsync),
    };

    public ToolDescriptor Descriptor { get; }

    public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
    {
        var (accepted, text) = await run(toolCall, ct).ConfigureAwait(false);
        return accepted ? ToolResult.Success(text) : ToolResult.Failed(ToolErrorCategory.InvalidArguments, text);
    }

    private static async Task<(bool, string)> ProposeAsync(ToolCall call, CancellationToken ct)
    {
        var arguments = call.Arguments;
        var proposal = new MemoryProposal(
            arguments.GetProperty("kind").GetString() == "decision" ? MemoryKind.Decision : MemoryKind.Note,
            arguments.GetProperty("subject").GetString()!, arguments.GetProperty("text").GetString()!,
            arguments.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
            arguments.TryGetProperty("replaces", out var replaces) ? [.. replaces.EnumerateArray().Select(id => id.GetInt64())] : null);
        var (id, text) = await call.Memory!.ProposeAsync(proposal, ct).ConfigureAwait(false);
        if (id is null)
        {
            return (false, text);
        }

        // The owner has just approved the call (MEM-03), so the owner's decision applies it.
        if (!call.Memory.OwnerApproves)
        {
            return (true, $"{text} The lead decides on it.");
        }

        var (applied, result) = await call.Memory.AsOwner().ApproveAsync(id.Value, "approved when proposed", ct).ConfigureAwait(false);
        return (true, applied ? $"{text} The owner approved it, so it is memory now." : $"{text} It is not memory yet: {result}");
    }

    private static async Task<(bool, string)> ReviewAsync(ToolCall call, CancellationToken ct)
    {
        var arguments = call.Arguments;
        var (id, reason) = (arguments.GetProperty("id").GetInt64(), arguments.GetProperty("reason").GetString()!);
        return arguments.GetProperty("approved").GetBoolean()
            ? await call.Memory!.ApproveAsync(id, reason, ct).ConfigureAwait(false)
            : await call.Memory!.RejectAsync(id, reason, ct).ConfigureAwait(false);
    }
}
