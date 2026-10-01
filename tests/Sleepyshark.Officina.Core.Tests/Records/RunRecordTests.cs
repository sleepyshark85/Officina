using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Records;

/// <summary>
/// The run record (REC): agents propose changes through the record tools, and the core validates and applies them. The
/// agent <c>dev</c> is offered the record tools as <c>fact</c>, <c>finding</c>, <c>decision</c> and <c>cite</c>, and
/// <c>edit</c>, whose gate allows it only once the record holds a decision (TOOL-06). Record tools run in parallel, so
/// each reply here makes one call, for a known order. Concurrent writers are in the storage contract tests.
/// </summary>
public class RunRecordTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // REC-01, REC-02.
    [Fact]
    public async Task A_proposal_is_applied_as_the_next_revision_attributed_to_the_agent()
    {
        var kit = Kit();
        kit.Model.CallTools(("fact", """{ "subject": "invoice.total", "value": "42", "source": "invoice.pdf", "asOf": "2026-09-30T00:00:00Z" }"""))
            .CallTools(("decision", """{ "subject": "currency", "choice": "EUR", "reason": "the invoice is from Berlin" }"""))
            .Reply("Done.");

        var result = await kit.RunAsync(Agent, "Read the invoice.", Ct);

        Assert.Equal(
            [
                new RecordEntry(result.Record[0].RunId, 1, Agent, kit.Time.GetUtcNow(), new Fact("invoice.total", "42", "invoice.pdf", new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero))),
                new RecordEntry(result.Record[0].RunId, 2, Agent, kit.Time.GetUtcNow(), new Decision("currency", "EUR", "the invoice is from Berlin", null)),
            ],
            result.Record);
        Assert.Equal(
            [
                "Recorded: r1 fact invoice.total = 42 (source: invoice.pdf; as of 2026-09-30 00:00:00Z)",
                "Recorded: r2 decision on currency: EUR, because the invoice is from Berlin (by dev)",
            ],
            ToolResults(result));
    }

    // REC-02: the reason goes back to the agent, and the record is unchanged.
    [Fact]
    public async Task A_proposal_that_does_not_validate_is_rejected_with_the_reason()
    {
        var kit = Kit();
        kit.Model.CallTools(("finding", """{ "text": "The total is wrong [cite:inv-1]." }"""))
            .CallTools(("cite", """{ "id": "inv-1", "document": "invoice.pdf", "location": "page 1", "quote": "Total: 42" }"""))
            .CallTools(("cite", """{ "id": "inv-1", "document": "order.pdf", "location": "page 2", "quote": "Total: 40" }"""))
            .CallTools(("decision", """{ "subject": "total", "choice": "42", "reason": "invoice", "replaces": 1 }"""))
            .CallTools(("finding", """{ "text": "The total is wrong [cite:inv-1]." }"""))
            .Reply("Done.");

        var result = await kit.RunAsync(Agent, "Check the invoice.", Ct);

        Assert.Equal(
            [
                "invalid arguments: it cites inv-1, which is not a citation in the record. Record the citation first.",
                "Recorded: r1 citation [cite:inv-1]: invoice.pdf, page 1: \"Total: 42\"",
                "invalid arguments: the citation id inv-1 is taken. Use another id.",
                "invalid arguments: r1 is not a decision in the record.",
                "Recorded: r2 finding: The total is wrong [cite:inv-1].",
            ],
            ToolResults(result));
        Assert.Equal(["citation", "finding"], result.Record.Select(entry => entry.Item.Kind));
    }

    // REC-03: neither value wins; a decision that replaces another ends their conflict.
    [Fact]
    public async Task Conflicting_values_and_decisions_are_both_kept_and_reported()
    {
        var kit = Kit();
        kit.Model.CallTools(("fact", """{ "subject": "total", "value": "42", "source": "invoice.pdf" }"""))
            .CallTools(("decision", """{ "subject": "db", "choice": "SQLite", "reason": "local" }"""))
            .CallTools(("fact", """{ "subject": "total", "value": "40", "source": "order.pdf" }"""))
            .CallTools(("decision", """{ "subject": "db", "choice": "Postgres", "reason": "scale" }"""))
            .CallTools(("decision", """{ "subject": "db", "choice": "Postgres", "reason": "agreed", "replaces": 2 }"""))
            .Reply("Done.");

        var result = await kit.RunAsync(Agent, "Reconcile.", Ct);

        Assert.Equal(5, result.Record.Length);
        Assert.Equal(
            [
                "Recorded: r3 fact total = 40 (source: order.pdf; as of 2000-01-01 00:00:00Z) CONFLICTS WITH r1",
                "Recorded: r4 decision on db: Postgres, because scale (by dev) CONFLICTS WITH r2",
                "Recorded: r5 decision on db: Postgres, because agreed (by dev; replaces r2)",
            ],
            ToolResults(result).Skip(2));
        Assert.Contains("r1 fact total = 42 (source: invoice.pdf; as of 2000-01-01 00:00:00Z) CONFLICTS WITH r3", Context(kit.Model.Requests[3]));
        Assert.Contains("r4 decision on db: Postgres, because scale (by dev) CONFLICTS WITH r2", Context(kit.Model.Requests[4]));
        Assert.Contains("r4 decision on db: Postgres, because scale (by dev)\n", Context(kit.Model.Requests[5]));
    }

    // CTX-07, CTX-01: facts first, each with its as-of time, in revision order; the rest after the retrieved passages.
    [Fact]
    public async Task The_records_entries_join_the_volatile_context_in_a_consistent_order()
    {
        var kit = Kit(agent => agent with
        {
            Context = new() { OperatingFacts = ["Today is {{now:date}}."], Retrieval = new() { BeforeTurn = ["handbook"] } },
        });
        kit.Model.CallTools(("fact", """{ "subject": "b", "value": "2", "source": "s" }"""))
            .CallTools(("finding", """{ "text": "found" }"""))
            .CallTools(("fact", """{ "subject": "a", "value": "1", "source": "s" }"""))
            .Reply("Done.");

        await kit.RunAsync(Agent, "Work.", Ct);

        Assert.Equal(
            """
            <context>
            <data source="record">
            r1 fact b = 2 (source: s; as of 2000-01-01 00:00:00Z)
            r3 fact a = 1 (source: s; as of 2000-01-01 00:00:00Z)
            </data>
            <data source="knowledge:handbook">
            coverage: covered
            hb-1: Refunds take 5 days.
            </data>
            <data source="record">
            r2 finding: found
            </data>
            Today is 2000-01-01.
            </context>
            """.ReplaceLineEndings("\n"),
            Context(kit.Model.Requests[3]));
    }

    // CTX-07: building the input reads the record but never changes it.
    [Fact]
    public async Task Building_the_input_never_changes_the_record()
    {
        var kit = Kit();
        kit.Model.CallTools(("fact", """{ "subject": "a", "value": "1", "source": "s" }"""))
            .Reply(new TextDelta("Part one."), new Stopped(StopReason.Paused))
            .Reply(new TextDelta("Part two."), new Stopped(StopReason.Paused))
            .Reply("Done.");

        var result = await kit.RunAsync(Agent, "Work.", Ct);

        Assert.Equal(4, kit.Model.Requests.Count);
        Assert.Equal(1, Assert.Single(result.Record).Revision);
        Assert.Equal(result.Record, await kit.Storage.Records.ReadAsync(null, result.Record[0].RunId, Ct));
    }

    // REC-06, CTX-01.
    [Fact]
    public async Task An_agent_sees_only_the_kinds_of_entry_it_is_configured_to()
    {
        var kit = Kit(agent => agent with { Context = new() { Record = ["decision"] } });
        kit.Model.CallTools(("fact", """{ "subject": "a", "value": "1", "source": "s" }"""))
            .CallTools(("decision", """{ "subject": "db", "choice": "SQLite", "reason": "local" }"""))
            .Reply("Done.");

        await kit.RunAsync(Agent, "Work.", Ct);

        Assert.Equal(
            "<context>\n<data source=\"record\">\nr2 decision on db: SQLite, because local (by dev)\n</data>\n</context>",
            Context(kit.Model.Requests[2]));
    }

    // TOOL-06.
    [Fact]
    public async Task A_gate_reads_the_record_to_require_a_prerequisite()
    {
        var kit = Kit(agent => agent with { HandOffOnPolicyGap = false });
        kit.Model.CallTools(("edit", """{ "line": "x = 1" }"""))
            .CallTools(("decision", """{ "subject": "x", "choice": "1", "reason": "spec" }"""))
            .CallTools(("edit", """{ "line": "x = 1" }"""))
            .Reply("Done.");

        var result = await kit.RunAsync(Agent, "Set x.", Ct);

        Assert.Equal(
            ["policy violation: a decision must come first", "Recorded: r1 decision on x: 1, because spec (by dev)", "ok"], ToolResults(result));
    }

    // LOOP-07: a proposal the record already holds is not progress.
    [Fact]
    public async Task Proposing_what_the_record_holds_again_is_a_stall()
    {
        var kit = Kit();
        for (var i = 0; i < 5; i++)
        {
            kit.Model.CallTools(("fact", """{ "subject": "a", "value": "1", "source": "s" }"""));
        }

        var result = await kit.RunAsync(Agent, "Work.", Ct);

        // The second call's result differs from the first's; the three after it make no progress.
        Assert.Equal((HandoffReason.NoProgress, 5), (result.Handoff!.Reason, result.Statistics.Iterations));
        Assert.Equal("Already in the record: r1 fact a = 1 (source: s; as of 2000-01-01 00:00:00Z)", ToolResults(result)[^1]);
        Assert.Single(result.Record);
    }

    private static List<string> ToolResults(AgentResult result) =>
        [.. result.Transcript.SelectMany(message => message.Content).OfType<ToolResultContent>()
            .Select(content => content.Text.Split('\n')[1])];

    /// <summary>The volatile context sent with a request.</summary>
    private static string Context(ModelRequest request) =>
        request.History.Last(message => message.Content is [TextContent { Text: var text }] && text.StartsWith("<context>", StringComparison.Ordinal))
            .Content.OfType<TextContent>().Single().Text;

    private static TestKit Kit(Func<AgentDefinition, AgentDefinition>? configure = null)
    {
        var options = Options(
            ("fact", new() { Source = "builtin:record.propose_fact" }),
            ("finding", new() { Source = "builtin:record.propose_finding" }),
            ("decision", new() { Source = "builtin:record.propose_decision" }),
            ("cite", new() { Source = "builtin:record.cite" }),
            ("edit", Extension("edit") with { Gates = ["decided"] })) with
        {
            Gates = new Dictionary<string, GateOptions> { ["decided"] = new() { Use = "extension:decided" } },
            Knowledge = new Dictionary<string, KnowledgeOptions> { ["handbook"] = new() { Use = "extension:handbook" } },
            Capabilities = new() { Knowledge = new() { Enabled = true } },
        };
        options = options with { Agents = new Dictionary<string, AgentDefinition> { [Agent] = (configure ?? (agent => agent))(options.Agents[Agent]) } };
        return new TestKit(
            options,
            new Dictionary<string, ITool> { ["edit"] = new FakeTool(ToolKind.Write) },
            new Dictionary<string, IGate> { ["decided"] = new DecisionFirst() },
            new Dictionary<string, IKnowledgeSource> { ["handbook"] = new FakeKnowledgeSource(Coverage.Covered, ("hb-1", "Refunds take 5 days.")) },
            new ProviderCapabilities { TurnScopedMessages = true }); // so every call carries the whole volatile context
    }

    /// <summary>Allows a call only once the run record holds a decision.</summary>
    private sealed class DecisionFirst : IGate
    {
        public async ValueTask<GateDecision> EvaluateAsync(GateContext context, CancellationToken ct) =>
            (await context.Record.ReadAsync(ct)).Any(entry => entry.Item is Decision) ? GateDecision.Allow : GateDecision.Deny("a decision must come first");
    }
}
