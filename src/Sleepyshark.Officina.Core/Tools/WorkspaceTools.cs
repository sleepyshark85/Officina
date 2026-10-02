using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// The tools that read, search and change files in an agent's working copy (WS-05, WS-06, WS-07), over
/// <see cref="IWorkingCopy"/>, so they work the same on the git workspace and the test kit's in-memory one (TEST-01). A
/// refused operation reaches the model as a failure with the workspace's reason.
/// </summary>
/// <param name="copyOf">The working copy a call works in (<see cref="ToolCall.WorkingCopy"/>), which the host opens when it is first needed.</param>
public sealed class WorkspaceTools(Func<ToolCall, Task<IWorkingCopy>> copyOf)
{
    public const string Read = "workspace.read_file";
    public const string Search = "workspace.search";
    public const string Edit = "workspace.edit_file";
    public const string Write = "workspace.write_file";
    public const string Delete = "workspace.delete_file";
    public const string Move = "workspace.move_file";

    private const string PathProperty = """ "path": { "type": "string", "description": "The file, relative to the working copy." } """;

    /// <summary>The tools, by the id that <c>extension:&lt;id&gt;</c> sources name.</summary>
    public IReadOnlyDictionary<string, ITool> Tools { get; } = new Dictionary<string, ITool>
    {
        [Read] = new Tool(
            "Reads a file, or only some of its lines.",
            $$"""{ "type": "object", "properties": { {{PathProperty}}, "firstLine": { "type": "integer", "minimum": 1 }, "lineCount": { "type": "integer", "minimum": 1 } }, "required": ["path"] }""",
            ToolKind.Read,
            copyOf,
            async (copy, arguments, ct) => await copy.ReadAsync(Text(arguments, "path"), Number(arguments, "firstLine"), Number(arguments, "lineCount"), ct).ConfigureAwait(false)),
        [Search] = new Tool(
            "Finds the lines that match a regular expression, in every file of the working copy.",
            """{ "type": "object", "properties": { "pattern": { "type": "string" } }, "required": ["pattern"] }""",
            ToolKind.Read,
            copyOf,
            async (copy, arguments, ct) => string.Join('\n', (await copy.SearchAsync(Text(arguments, "pattern"), ct).ConfigureAwait(false)).Select(hit => $"{hit.Path}:{hit.Line}: {hit.Text}"))),
        [Edit] = new Tool(
            "Replaces the one place where oldText occurs in a file you have read since it last changed.",
            $$"""{ "type": "object", "properties": { {{PathProperty}}, "oldText": { "type": "string", "minLength": 1 }, "newText": { "type": "string" } }, "required": ["path", "oldText", "newText"] }""",
            ToolKind.Write,
            copyOf,
            async (copy, arguments, ct) =>
            {
                await copy.EditAsync(Text(arguments, "path"), Text(arguments, "oldText"), Text(arguments, "newText"), ct).ConfigureAwait(false);
                return "Edited.";
            }),
        [Write] = new Tool(
            "Creates a file, or replaces one you have read since it last changed.",
            $$"""{ "type": "object", "properties": { {{PathProperty}}, "content": { "type": "string" } }, "required": ["path", "content"] }""",
            ToolKind.Write,
            copyOf,
            async (copy, arguments, ct) =>
            {
                await copy.WriteAsync(Text(arguments, "path"), Text(arguments, "content"), ct).ConfigureAwait(false);
                return "Written.";
            }),
        [Delete] = new Tool(
            "Deletes a file you have read since it last changed.",
            $$"""{ "type": "object", "properties": { {{PathProperty}} }, "required": ["path"] }""",
            ToolKind.Write,
            copyOf,
            async (copy, arguments, ct) =>
            {
                await copy.DeleteAsync(Text(arguments, "path"), ct).ConfigureAwait(false);
                return "Deleted.";
            }),
        [Move] = new Tool(
            "Moves or renames a file you have read since it last changed, to a path where no file exists.",
            """{ "type": "object", "properties": { "from": { "type": "string", "description": "The file, relative to the working copy." }, "to": { "type": "string", "description": "Where it goes, relative to the working copy." } }, "required": ["from", "to"] }""",
            ToolKind.Write,
            copyOf,
            async (copy, arguments, ct) =>
            {
                await copy.MoveAsync(Text(arguments, "from"), Text(arguments, "to"), ct).ConfigureAwait(false);
                return "Moved.";
            }),
    };

    private static string Text(JsonElement arguments, string name) => arguments.GetProperty(name).GetString()!;

    private static int? Number(JsonElement arguments, string name) => arguments.TryGetProperty(name, out var value) ? value.GetInt32() : null;

    private sealed class Tool(
        string description, string inputSchema, ToolKind kind, Func<ToolCall, Task<IWorkingCopy>> copyOf,
        Func<IWorkingCopy, JsonElement, CancellationToken, Task<string>> invoke) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(description, JsonDocument.Parse(inputSchema).RootElement.Clone(), kind, ParallelSafe: kind == ToolKind.Read);

        public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
        {
            // TASK-06: a task's copy holds its assignee's work, which the reviewer reads and never changes.
            if (Descriptor.Kind == ToolKind.Write && toolCall.Board is { TaskId: { } taskId } board
                && (await board.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(task => task.Id == taskId) is { } task && task.Assignee != toolCall.Agent)
            {
                return ToolResult.Failed(ToolErrorCategory.NotAuthorised, $"only the agent working on task {taskId} changes its working copy");
            }

            try
            {
                var copy = await copyOf(toolCall).ConfigureAwait(false);
                return ToolResult.Success(await invoke(copy, toolCall.Arguments, ct).ConfigureAwait(false));
            }
            catch (WorkspaceException exception)
            {
                return ToolResult.Failed(ToolErrorCategory.Failed, exception.Message);
            }
        }
    }
}
