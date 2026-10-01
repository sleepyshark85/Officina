using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>The tool behind a <c>knowledge:&lt;name&gt;</c> source: the agent searches a knowledge source when it chooses (CTX-04).</summary>
/// <param name="name">The knowledge source's name in <c>knowledge</c>.</param>
/// <param name="source">The application's source.</param>
internal sealed class KnowledgeTool(string name, IKnowledgeSource source) : ITool
{
    /// <summary>The most passages one search returns, before the turn or as a tool.</summary>
    public const int MaxPassages = 8;

    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{ "type": "object", "properties": { "question": { "type": "string" } }, "required": ["question"] }""").RootElement.Clone();

    public ToolDescriptor Descriptor { get; } = new(
        $"Searches the knowledge source {name}. Returns passages with their citations, and whether they cover the question.",
        Schema, ToolKind.Read, ParallelSafe: true);

    public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
    {
        var question = toolCall.Arguments.GetProperty("question").GetString()!;
        var retrieval = await source.RetrieveAsync(new RetrievalQuery(question, toolCall.Caller, MaxPassages), ct).ConfigureAwait(false);
        return ToolResult.Success(Format(retrieval));
    }

    /// <summary>What a source found: its coverage (CTX-05), then each passage after its citation. The caller labels it as data (INV-08).</summary>
    public static string Format(Retrieval retrieval) =>
        string.Join('\n', retrieval.Passages.Select(passage => $"{passage.Citation}: {passage.Text}")
            .Prepend($"coverage: {JsonNamingPolicy.CamelCase.ConvertName(retrieval.Coverage.ToString())}"));
}
