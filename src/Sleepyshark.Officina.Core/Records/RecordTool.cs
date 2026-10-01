using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Records;

/// <summary>
/// A built-in record tool, through which agents propose changes to the run record (REC-02). It declares itself a read:
/// it changes only the run record, which the core validates and keeps with each entry's agent and revision, so it needs
/// no gate, and proposals are safe to make in parallel (REC-04).
/// </summary>
internal sealed class RecordTool : ITool
{
    private const string Text = """{ "type": "string", "minLength": 1 }""";
    private const string Cite = "Cite a citation in the record as [cite:<id>].";

    private readonly Func<JsonElement, RecordItem> read;

    private RecordTool(string description, string properties, string required, Func<JsonElement, RecordItem> read)
    {
        var schema = $$"""{ "type": "object", "properties": { {{properties}} }, "required": [{{required}}] }""";
        Descriptor = new(description, JsonDocument.Parse(schema).RootElement.Clone(), ToolKind.Read, ParallelSafe: true);
        this.read = read;
    }

    /// <summary>The record tools, by the name <c>builtin:</c> sources use.</summary>
    public static IReadOnlyDictionary<string, ITool> All { get; } = new Dictionary<string, ITool>
    {
        ["record.propose_fact"] = new RecordTool(
            $"Proposes a fact for the run record: a value of a subject, where it comes from, and when it was true (now, if not given). {Cite}",
            $$""" "subject": {{Text}}, "value": {{Text}}, "source": {{Text}}, "asOf": { "type": "string", "format": "date-time" } """,
            """ "subject", "value", "source" """,
            arguments => new Fact(
                Get(arguments, "subject"), Get(arguments, "value"), Get(arguments, "source"),
                arguments.TryGetProperty("asOf", out var asOf) ? asOf.GetDateTimeOffset() : null)),
        ["record.propose_finding"] = new RecordTool(
            $"Proposes a finding for the run record: something learned that is not a single value. {Cite}",
            $$""" "text": {{Text}} """,
            """ "text" """,
            arguments => new Finding(Get(arguments, "text"))),
        ["record.propose_decision"] = new RecordTool(
            $"Proposes a decision for the run record: the choice made on a subject, why, and the revision of the decision it replaces, if any. {Cite}",
            $$""" "subject": {{Text}}, "choice": {{Text}}, "reason": {{Text}}, "replaces": { "type": "integer" } """,
            """ "subject", "choice", "reason" """,
            arguments => new Decision(
                Get(arguments, "subject"), Get(arguments, "choice"), Get(arguments, "reason"),
                arguments.TryGetProperty("replaces", out var replaces) ? replaces.GetInt64() : null)),
        ["record.cite"] = new RecordTool(
            "Records a citation: a short id of your choice, the document, the location in it, and the words quoted. Answers and record entries then cite it as [cite:<id>].",
            $$""" "id": { "type": "string", "pattern": "^[A-Za-z0-9_.:#-]+$" }, "document": {{Text}}, "location": {{Text}}, "quote": {{Text}} """,
            """ "id", "document", "location", "quote" """,
            arguments => new Citation(Get(arguments, "id"), Get(arguments, "document"), Get(arguments, "location"), Get(arguments, "quote"))),
    };

    public ToolDescriptor Descriptor { get; }

    public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct)
    {
        RecordItem item;
        try
        {
            item = read(toolCall.Arguments);
        }
        catch (FormatException)
        {
            return ToolResult.Failed(ToolErrorCategory.InvalidArguments, "asOf must be a date and time, such as 2026-10-01T09:00:00Z");
        }

        var (accepted, text) = await toolCall.Record.ProposeAsync(item, ct).ConfigureAwait(false);
        return accepted ? ToolResult.Success(text) : ToolResult.Failed(ToolErrorCategory.InvalidArguments, text);
    }

    private static string Get(JsonElement arguments, string name) => arguments.GetProperty(name).GetString()!;
}
