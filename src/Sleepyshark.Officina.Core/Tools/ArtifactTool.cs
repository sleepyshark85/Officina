using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>The built-in tool that pages through a run's artifact, such as the full text of a trimmed tool result (TOOL-09).</summary>
/// <param name="store">Where the run's artifacts are kept.</param>
internal sealed class ArtifactTool(IArtifactStore store) : ITool
{
    /// <summary>The tool's name in <c>builtin:</c> sources.</summary>
    public const string Name = "artifact.page";

    /// <summary>The most characters one call returns.</summary>
    public const int PageLength = 8_000;

    public ToolDescriptor Descriptor { get; } = new(
        $"Reads up to {PageLength} characters of an artifact, such as the full text of a trimmed tool result, from an offset.",
        JsonDocument.Parse("""
            { "type": "object", "properties": { "artifact": { "type": "integer" }, "offset": { "type": "integer", "minimum": 0 } }, "required": ["artifact"] }
            """).RootElement.Clone(),
        ToolKind.Read, ParallelSafe: true);

    public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
    {
        var id = toolCall.Arguments.GetProperty("artifact").GetInt64();
        var offset = toolCall.Arguments.TryGetProperty("offset", out var from) ? from.GetInt32() : 0;
        if (await store.ReadAsync(toolCall.Caller.Tenant, toolCall.Record.RunId, id, ct).ConfigureAwait(false) is not { } artifact)
        {
            return ToolResult.Failed(ToolErrorCategory.InvalidArguments, $"the run has no artifact {id}");
        }

        var content = artifact.Content;
        if (offset > content.Length)
        {
            return ToolResult.Failed(ToolErrorCategory.InvalidArguments, $"artifact {id} has {content.Length} characters");
        }

        var length = Math.Min(content.Length - offset, PageLength);
        return ToolResult.Success($"{content.Substring(offset, length)}\n[Characters {offset} to {offset + length} of {content.Length}.]");
    }
}
