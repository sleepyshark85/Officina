using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tasks;

/// <summary>
/// A built-in task tool, through which agents act on the run's task board (TASK). Like the record tools, it declares
/// itself a read: it changes only the board, which checks every change against its rules and keeps it with its agent
/// and revision, so it needs no gate, and calls are safe to make in parallel.
/// </summary>
internal sealed class TaskTool : ITool
{
    private const string Text = """{ "type": "string", "minLength": 1 }""";
    private const string Texts = """{ "type": "array", "items": { "type": "string", "minLength": 1 } }""";
    private const string Reason = $$""" "reason": {{Text}} """;

    /// <summary>The fields an agent may set on a task it creates, and change on one that exists.</summary>
    private const string Fields = $$"""
        "title": {{Text}}, "description": { "type": "string" }, "acceptanceCriteria": {{Texts}}, "role": {{Text}}, "dependsOn": {{Texts}},
        "priority": { "type": "integer" }
        """;

    private readonly Func<TaskBoard, JsonElement, CancellationToken, Task<(bool Accepted, string Text)>> run;

    private TaskTool(string description, string properties, string required, Func<TaskBoard, JsonElement, CancellationToken, Task<(bool, string)>> run)
    {
        var schema = $$"""{ "type": "object", "properties": { "id": {{Text}}{{(properties.Length > 0 ? $", {properties}" : "")}} }, "required": ["id"{{required}}], "additionalProperties": false }""";
        Descriptor = new(description, JsonDocument.Parse(schema).RootElement.Clone(), ToolKind.Read, ParallelSafe: true);
        this.run = run;
    }

    /// <summary>The names of the task tools in <c>builtin:</c> sources.</summary>
    public static IReadOnlyList<string> Names { get; } = ["tasks.create", "tasks.update", "tasks.claim", "tasks.submit_for_review", "tasks.review"];

    public ToolDescriptor Descriptor { get; }

    /// <summary>The task tool with a name from <see cref="Names"/>.</summary>
    /// <param name="name">The tool's name.</param>
    /// <param name="checks">The application's checks, by extension id, which a submitted task's checks name.</param>
    public static TaskTool Create(string name, IReadOnlyDictionary<string, ICheck> checks) => name switch
    {
        "tasks.create" => new(
            "Adds a task to the board: its id, title, what it is, its acceptance criteria, the checks that must pass before it is reviewed, the role it needs, the tasks it depends on, its priority (higher first), whether it needs a review, and why. It is ready once its dependencies are done.",
            $$""" {{Fields}}, "checks": {{Texts}}, "requiresReview": { "type": "boolean" }, {{Reason}} """, """, "title", "reason" """,
            (board, arguments, ct) => board.AddAsync(Get(arguments, "id"), Edit(arguments), Get(arguments, "reason"), ct)),
        "tasks.update" => new(
            "Changes a task's fields, or blocks it, or unblocks or retries it by setting its state to ready, and says why. The team's lead also assigns a task to an agent of the team, and cancels a task that is no longer needed.",
            $$""" {{Fields}}, "assignee": {{Text}}, "state": { "enum": ["blocked", "ready", "cancelled"] }, {{Reason}} """, """, "reason" """,
            (board, arguments, ct) => board.EditAsync(Get(arguments, "id"), Edit(arguments), Get(arguments, "reason"), ct)),
        "tasks.claim" => new(
            "Takes a ready task to work on. Only one agent works on a task at a time.", "", "",
            (board, arguments, ct) => board.ClaimAsync(Get(arguments, "id"), ct)),
        "tasks.submit_for_review" => new(
            "Submits your task's work, with what it produced, such as artifact ids or files. Its checks run, and only if they all pass is it in review.",
            $$""" "artifacts": {{Texts}} """, "",
            (board, arguments, ct) => board.SubmitAsync(
                Get(arguments, "id"), arguments.TryGetProperty("artifacts", out var artifacts) ? [.. artifacts.EnumerateArray().Select(artifact => artifact.GetString()!)] : [],
                checks, ct)),
        "tasks.review" => new(
            "Reviews another agent's task in review: approves it, or asks for changes, and gives the reasons.",
            $$""" "approved": { "type": "boolean" }, "reasons": {{Text}} """, """, "approved", "reasons" """,
            (board, arguments, ct) => board.ReviewAsync(Get(arguments, "id"), arguments.GetProperty("approved").GetBoolean(), Get(arguments, "reasons"), ct)),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a task tool"),
    };

    public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
    {
        var (accepted, text) = await run(toolCall.Board!, toolCall.Arguments, ct).ConfigureAwait(false);
        return accepted ? ToolResult.Success(text) : ToolResult.Failed(ToolErrorCategory.InvalidArguments, text);
    }

    private static string Get(JsonElement arguments, string name) => arguments.GetProperty(name).GetString()!;

    /// <summary>The task's fields among the arguments; the others, such as its id and the reason, are left out.</summary>
    private static TaskEdit Edit(JsonElement arguments) => arguments.Deserialize<TaskEdit>(TaskBoard.Json)!;
}
