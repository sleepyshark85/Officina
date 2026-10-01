using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Records;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>The tool behind a <c>knowledge:&lt;name&gt;</c> source: the agent searches a knowledge source when it chooses (CTX-04).</summary>
/// <param name="name">The knowledge source's name in <c>knowledge</c>.</param>
/// <param name="source">The application's source.</param>
/// <param name="mask">Whether the source's passages are masked (ING-02).</param>
internal sealed class KnowledgeTool(string name, IKnowledgeSource source, bool mask) : ITool
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
        await CiteAsync(toolCall.Record, name, retrieval, mask, ct).ConfigureAwait(false);
        return ToolResult.Success(Format(retrieval));
    }

    /// <summary>
    /// Records each passage as a citation in the run record, by the id the source gives it, so answers can cite it as
    /// <c>[cite:id]</c> (OUT-04, REC-01). A passage already recorded is not added again; one whose id the record holds
    /// for other words is left out, as any other citation with a taken id. The quotes of a masked source's passages are
    /// masked before they reach the record (ING-02).
    /// </summary>
    public static async Task CiteAsync(RunRecord record, string name, Retrieval retrieval, bool mask, CancellationToken ct)
    {
        foreach (var passage in retrieval.Passages)
        {
            await record.ProposeAsync(new Citation(passage.Citation, $"knowledge:{name}", passage.Citation, mask ? record.Mask(passage.Text) : passage.Text), ct).ConfigureAwait(false);
        }
    }

    /// <summary>What a source found: its coverage (CTX-05), then each passage after its citation. The caller labels it as data (INV-08).</summary>
    public static string Format(Retrieval retrieval) =>
        string.Join('\n', retrieval.Passages.Select(passage => $"{passage.Citation}: {passage.Text}")
            .Prepend($"coverage: {JsonNamingPolicy.CamelCase.ConvertName(retrieval.Coverage.ToString())}"));
}
