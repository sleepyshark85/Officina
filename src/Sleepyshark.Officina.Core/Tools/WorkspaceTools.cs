using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// The tools that read, search and change files in one agent's working copy (WS-05, WS-06, WS-07), over
/// <see cref="IWorkingCopy"/>, so they work the same on the git workspace and the test kit's in-memory one (TEST-01). A
/// refused operation reaches the model as a failure with the workspace's reason.
/// </summary>
/// <param name="copy">The agent's working copy.</param>
public sealed class WorkspaceTools(IWorkingCopy copy)
{
    public const string Read = "workspace.read_file";
    public const string Search = "workspace.search";
    public const string Edit = "workspace.edit_file";
    public const string Write = "workspace.write_file";

    private const string PathProperty = """ "path": { "type": "string", "description": "The file, relative to the working copy." } """;

    /// <summary>The tools, by the id that <c>extension:&lt;id&gt;</c> sources name.</summary>
    public IReadOnlyDictionary<string, ITool> Tools { get; } = new Dictionary<string, ITool>
    {
        [Read] = new Tool(
            "Reads a file, or only some of its lines.",
            $$"""{ "type": "object", "properties": { {{PathProperty}}, "firstLine": { "type": "integer", "minimum": 1 }, "lineCount": { "type": "integer", "minimum": 1 } }, "required": ["path"] }""",
            ToolKind.Read,
            async (arguments, ct) => await copy.ReadAsync(Text(arguments, "path"), Number(arguments, "firstLine"), Number(arguments, "lineCount"), ct).ConfigureAwait(false)),
        [Search] = new Tool(
            "Finds the lines that match a regular expression, in every file of the working copy.",
            """{ "type": "object", "properties": { "pattern": { "type": "string" } }, "required": ["pattern"] }""",
            ToolKind.Read,
            async (arguments, ct) => string.Join('\n', (await copy.SearchAsync(Text(arguments, "pattern"), ct).ConfigureAwait(false)).Select(hit => $"{hit.Path}:{hit.Line}: {hit.Text}"))),
        [Edit] = new Tool(
            "Replaces the one place where oldText occurs in a file you have read since it last changed.",
            $$"""{ "type": "object", "properties": { {{PathProperty}}, "oldText": { "type": "string", "minLength": 1 }, "newText": { "type": "string" } }, "required": ["path", "oldText", "newText"] }""",
            ToolKind.Write,
            async (arguments, ct) =>
            {
                await copy.EditAsync(Text(arguments, "path"), Text(arguments, "oldText"), Text(arguments, "newText"), ct).ConfigureAwait(false);
                return "Edited.";
            }),
        [Write] = new Tool(
            "Creates a file, or replaces one you have read since it last changed.",
            $$"""{ "type": "object", "properties": { {{PathProperty}}, "content": { "type": "string" } }, "required": ["path", "content"] }""",
            ToolKind.Write,
            async (arguments, ct) =>
            {
                await copy.WriteAsync(Text(arguments, "path"), Text(arguments, "content"), ct).ConfigureAwait(false);
                return "Written.";
            }),
    };

    private static string Text(JsonElement arguments, string name) => arguments.GetProperty(name).GetString()!;

    private static int? Number(JsonElement arguments, string name) => arguments.TryGetProperty(name, out var value) ? value.GetInt32() : null;

    private sealed class Tool(string description, string inputSchema, ToolKind kind, Func<JsonElement, CancellationToken, Task<string>> invoke) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(description, JsonDocument.Parse(inputSchema).RootElement.Clone(), kind, ParallelSafe: kind == ToolKind.Read);

        public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
        {
            try
            {
                return ToolResult.Success(await invoke(toolCall.Arguments, ct).ConfigureAwait(false));
            }
            catch (WorkspaceException exception)
            {
                return ToolResult.Failed(ToolErrorCategory.Failed, exception.Message);
            }
        }
    }
}
