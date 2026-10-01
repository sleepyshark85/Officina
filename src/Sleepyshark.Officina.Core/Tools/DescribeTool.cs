using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// Describes an agent's tools when their descriptions are loaded on demand (TOOL-12). The description arrives as a tool
/// result, in the history, so the offered tools, and with them the stable prefix, never change.
/// </summary>
/// <param name="tools">The agent's tools, by name.</param>
internal sealed class DescribeTool(IReadOnlyDictionary<string, CatalogTool> tools) : ITool
{
    /// <summary>What every other tool is offered with in place of its description and schema.</summary>
    public static readonly string Stub = $"Call {OfficinaOptions.DescribeTool} with this tool's name for its description and arguments before calling it.";

    /// <summary>Any arguments: the pipeline still validates them against the tool's own schema.</summary>
    public static readonly JsonElement AnyArguments = JsonDocument.Parse("""{ "type": "object" }""").RootElement.Clone();

    public ToolDescriptor Descriptor { get; } = new(
        "Returns a tool's description and the JSON Schema of its arguments.",
        JsonDocument.Parse("""{ "type": "object", "properties": { "name": { "type": "string" } }, "required": ["name"] }""").RootElement.Clone(),
        ToolKind.Read, ParallelSafe: true);

    public ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
    {
        var name = toolCall.Arguments.GetProperty("name").GetString()!;
        if (!tools.TryGetValue(name, out var tool) || tool.Implementation is not { } implementation || implementation == this)
        {
            return ValueTask.FromResult(ToolResult.Failed(ToolErrorCategory.UnknownTool, name));
        }

        var description = new JsonObject
        {
            ["name"] = name,
            ["description"] = implementation.Descriptor.Description,
            ["inputSchema"] = JsonNode.Parse(implementation.Descriptor.InputSchema.GetRawText()),
        };
        return ValueTask.FromResult(ToolResult.Success(description.ToJsonString()));
    }
}
