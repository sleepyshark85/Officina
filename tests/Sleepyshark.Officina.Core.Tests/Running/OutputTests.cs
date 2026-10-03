using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>
/// What output must be before a turn completes with it (OUT, INV-09). The agent <c>dev</c> is offered <c>cite</c>, the
/// record tool, and <c>report</c>, whose result carries an artifact. The checks <c>first</c>, <c>second</c> and
/// <c>third</c> pass unless the output contains their name after <c>fail:</c>.
/// </summary>
public class OutputTests
{
    private const string Schema = """{ "type": "object", "properties": { "total": { "type": "number" } }, "required": ["total"] }""";

    private static readonly string[] CheckNames = ["first", "second", "third"];

    private readonly ConcurrentQueue<(string Check, CheckContext Context)> ran = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // OUT-01, CLD-06: the request carries the schema, for a provider that constrains output natively; the core checks it either way.
    [Fact]
    public async Task Structured_output_that_matches_its_schema_completes_the_turn()
    {
        var kit = Kit(Structured);
        kit.Model.Reply("""{ "total": 42 }""");

        Assert.Equal((AgentOutcome.Completed, """{ "total": 42 }"""), Outcome(await kit.RunAsync(Agent, "Total?", Ct)));
        Assert.Equal("number", Assert.Single(kit.Model.Requests).OutputSchema!.Value.GetProperty("properties").GetProperty("total").GetProperty("type").GetString());
    }

    // OUT-02, TEST-07.
    [Fact]
    public async Task Invalid_structured_output_goes_back_with_its_errors_twice_then_is_handed_off()
    {
        var kit = Kit(Structured);
        kit.Model.Reply("""{ "total": "42" }""").Reply("forty-two").Reply("{}");

        var result = await kit.RunAsync(Agent, "Total?", Ct);

        Assert.Equal(
            (HandoffReason.InvalidStructuredOutput, "the output does not match its JSON Schema after 3 replies: /: Required properties [\"total\"] are not present"),
            (result.Handoff!.Reason, result.Handoff.Detail));
        Assert.Equal(3, kit.Model.Requests.Count);
        Assert.Contains(
            "/total: Value is \"string\" but should be \"number\"",
            Assert.IsType<TextContent>(Assert.Single(kit.Model.Requests[1].History[^1].Content)).Text);
        Assert.Equal(Message.User($"The output does not match its JSON Schema: it is not JSON.\n{ToldSchema}"), kit.Model.Requests[2].History[^1]);
    }

    // OUT-02: the next attempt gets the exact errors and the schema, and a corrected reply completes the turn.
    [Fact]
    public async Task Structured_output_of_the_wrong_shape_is_corrected_from_its_errors_and_schema()
    {
        var kit = Kit(agent => Structured(agent) with { Output = Structured(agent).Output with { Attempts = 1 } });
        kit.Model.Reply("""{ "total": { "amount": 42 } }""").Reply("""{ "total": 42 }""");

        var result = await kit.RunAsync(Agent, "Total?", Ct);

        Assert.Equal((AgentOutcome.Completed, """{ "total": 42 }"""), Outcome(result));
        var retry = Assert.IsType<TextContent>(Assert.Single(kit.Model.Requests[1].History[^1].Content)).Text;
        Assert.StartsWith("The output does not match its JSON Schema: ", retry, StringComparison.Ordinal);
        Assert.Contains("; /total: Value is \"object\" but should be \"number\".\n", retry, StringComparison.Ordinal);
        Assert.EndsWith($".\n{ToldSchema}", retry, StringComparison.Ordinal);
    }

    // OUT-01: the schema is shown as written, with no \uXXXX escapes; with no attempts left, one invalid reply is handed off.
    [Fact]
    public async Task The_schema_is_told_as_written_and_one_invalid_reply_is_handed_off_with_no_attempts()
    {
        const string schema = """{"type":"object","properties":{"total":{"type":"number","description":"Total in € of <net> & 'gross'"}},"required":["total"]}""";
        var kit = Kit(agent => agent with { Output = new() { Format = OutputFormat.Structured, Schema = schema, Attempts = 0 } });
        kit.Model.Reply("{}");

        var result = await kit.RunAsync(Agent, "Total?", Ct);

        Assert.Contains($"matching this JSON Schema:\n{schema}", Assert.Single(kit.Model.Requests).Instructions, StringComparison.Ordinal);
        Assert.StartsWith("the output does not match its JSON Schema after 1 reply: ", result.Handoff!.Detail, StringComparison.Ordinal);
    }

    // OUT-01, CLD-06: the model is told the schema in its instructions, which are cached, unless the provider constrains the output to it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_model_is_told_the_schema_unless_the_provider_enforces_it(bool enforced)
    {
        var kit = Kit(Structured, structuredOutput: enforced);
        kit.Model.Reply("""{ "total": 42 }""");

        await kit.RunAsync(Agent, "Total?", Ct);

        var request = Assert.Single(kit.Model.Requests);
        Assert.Equal(!enforced, request.Instructions.Contains(ToldSchema, StringComparison.Ordinal));
        Assert.StartsWith("Work.\n\n", request.Instructions, StringComparison.Ordinal);
        Assert.NotNull(request.OutputSchema);
    }

    // OUT-03: in order, and the first failure decides.
    [Fact]
    public async Task Output_checks_run_in_order_and_the_first_failure_hands_the_turn_off()
    {
        var kit = Kit(agent => agent with { Output = new() { Checks = ["first", "second", "third"] } });
        kit.Model.Reply("fail:second fail:third");

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((HandoffReason.OutputCheckFailed, "check second failed: found fail:second"), (result.Handoff!.Reason, result.Handoff.Detail));
        Assert.Equal(["first", "second"], ran.Select(check => check.Check));
    }

    // LOOP-05: the stop condition "the output passes its checks".
    [Fact]
    public async Task With_the_checks_as_stop_condition_the_turn_completes_once_its_output_passes_them()
    {
        var kit = Kit(agent => agent with { Output = new() { Checks = ["first"] }, StopWhen = new() { Finished = false, ChecksPass = true } });
        kit.Model.Reply(new TextDelta("fail:first"), CallReport(), new Stopped(StopReason.WantsTools))
            .Reply(new TextDelta("All green."), CallReport(), new Stopped(StopReason.WantsTools));

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.Completed, "All green.", 2), (result.Outcome, result.Output, result.Statistics.Iterations));
    }

    // INV-09: every way a turn completes runs the checks, and only their result decides.
    [Theory]
    [InlineData("finished")]
    [InlineData("finishTool")]
    [InlineData("maxIterations")]
    [InlineData("checksPass")]
    public async Task No_stop_condition_completes_a_turn_whose_output_fails_a_check(string condition)
    {
        var stop = condition switch
        {
            "finishTool" => new StopConditions { Finished = false, FinishTool = "report" },
            "maxIterations" => new StopConditions { MaxIterations = 1 },
            "checksPass" => new StopConditions { Finished = false, ChecksPass = true },
            _ => new StopConditions(),
        };
        var kit = Kit(agent => agent with { Output = new() { Checks = ["first"] }, StopWhen = stop });
        if (condition == "finishTool")
        {
            kit.Model.CallTools(("report", """{ "verdict": "fail:first" }"""));
        }
        else if (condition == "maxIterations")
        {
            kit.Model.Reply(new TextDelta("I am sure it passes. fail:first"), CallReport(), new Stopped(StopReason.WantsTools));
        }
        else
        {
            kit.Model.Reply("I am sure it passes. fail:first");
        }

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.HandedOff, HandoffReason.OutputCheckFailed), (result.Outcome, result.Handoff!.Reason));
    }

    // OUT-04.
    [Theory]
    [InlineData(CitationRule.Off, "Total 42 [cite:missing].", AgentOutcome.Completed)]
    [InlineData(CitationRule.Resolve, "Total 42.", AgentOutcome.Completed)]
    [InlineData(CitationRule.Resolve, "Total 42 [cite:inv].", AgentOutcome.Completed)]
    [InlineData(CitationRule.Resolve, "Total 42 [cite:inv] [cite:missing].", AgentOutcome.HandedOff)]
    [InlineData(CitationRule.Required, "Total 42.", AgentOutcome.HandedOff)]
    [InlineData(CitationRule.Required, "Total 42 [cite:inv].", AgentOutcome.Completed)]
    public async Task Citation_rules_check_what_the_output_cites_against_the_run_record(CitationRule rule, string output, AgentOutcome outcome)
    {
        var kit = Kit(agent => agent with { Output = new() { Citations = rule } });
        kit.Model.CallTools(("cite", """{ "id": "inv", "document": "invoice.pdf", "location": "page 1", "quote": "Total: 42" }""")).Reply(output);

        var result = await kit.RunAsync(Agent, "Total?", Ct);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(outcome == AgentOutcome.HandedOff ? HandoffReason.OutputCheckFailed : null, result.Handoff?.Reason);
    }

    // OUT-04: a passage retrieved before the turn is a citation in the record.
    [Theory]
    [InlineData("Total 42 [cite:hb-1].", null)]
    [InlineData("Total 42 [cite:missing].", "the output cites missing, which is not a citation in the run record")]
    public async Task Citations_resolve_by_default_when_a_knowledge_source_is_configured(string output, string? problem)
    {
        var kit = Kit(agent => agent with { Context = new() { Retrieval = new() { BeforeTurn = ["handbook"] } } }, knowledge: true);
        kit.Model.Reply(output);

        var result = await kit.RunAsync(Agent, "Total?", Ct);

        Assert.Equal(problem, result.Handoff?.Detail);
    }

    // OUT-05: the result carries the artifacts, and the checks see them.
    [Fact]
    public async Task A_result_carries_the_artifacts_its_tool_calls_produced()
    {
        var kit = Kit(agent => agent with { Output = new() { Checks = ["first"] } });
        kit.Model.CallTools(("report", """{ "verdict": "ok" }""")).Reply("See the report.");

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal([new Artifact("report.md", "# Report")], result.Artifacts);
        Assert.Equal([new Artifact("report.md", "# Report")], Assert.Single(ran).Context.Artifacts);
        Assert.Equal("See the report.", ran.Single().Context.Output);
    }

    [Fact]
    public void A_check_the_application_has_not_registered_is_reported()
    {
        var options = Options() with { Checks = new Dictionary<string, CheckOptions> { ["style"] = new() { Use = "extension:Style" } } };

        var error = Assert.Throws<ConfigurationException>(() => new TestKit(options));

        Assert.Equal(("checks.style.use", "check extension \"Style\" is not registered."), (Assert.Single(error.Errors).Path, error.Errors[0].Problem));
    }

    private const string ToldSchema = "Reply with JSON only, matching this JSON Schema:\n" + """{"type":"object","properties":{"total":{"type":"number"}},"required":["total"]}""";

    private static AgentDefinition Structured(AgentDefinition agent) => agent with { Output = new() { Format = OutputFormat.Structured, Schema = Schema } };

    private static (AgentOutcome, string) Outcome(AgentResult result) => (result.Outcome, result.Output);

    private static ContentReceived CallReport() =>
        new(new ToolUseContent($"call-{Guid.NewGuid()}", "report", Args("""{ "verdict": "ok" }""")));

    /// <param name="configure">Changes the agent's definition.</param>
    /// <param name="knowledge">Whether the knowledge source <c>handbook</c> is on.</param>
    /// <param name="structuredOutput">Whether the provider's structured output feature is on; the model has it.</param>
    private TestKit Kit(Func<AgentDefinition, AgentDefinition>? configure = null, bool knowledge = false, bool structuredOutput = false)
    {
        var options = Options(
            ("cite", new() { Source = "builtin:record.cite" }),
            ("report", Extension("report") with { ParallelSafe = false })) with
        {
            Checks = CheckNames.ToDictionary(name => name, name => new CheckOptions { Use = $"extension:{name}" }),
            Knowledge = knowledge
                ? new Dictionary<string, KnowledgeOptions> { ["handbook"] = new() { Use = "extension:handbook" } }
                : new Dictionary<string, KnowledgeOptions>(),
            Capabilities = new() { Knowledge = new() { Enabled = knowledge } },
        };
        options = options with
        {
            Agents = new Dictionary<string, AgentDefinition> { [Agent] = (configure ?? (agent => agent))(options.Agents[Agent]) },
            Providers = options.Providers.ToDictionary(provider => provider.Key, provider => provider.Value with { Features = new() { StructuredOutput = structuredOutput } }),
        };
        return new TestKit(
            options,
            new Dictionary<string, ITool>
            {
                ["report"] = new FakeTool(ToolKind.Read, run: (_, _) => ValueTask.FromResult(ToolResult.Success("written", new Artifact("report.md", "# Report")))),
            },
            knowledge: new Dictionary<string, IKnowledgeSource> { ["handbook"] = new FakeKnowledgeSource(Coverage.Covered, ("hb-1", "Total: 42")) },
            checks: options.Checks.Keys.ToDictionary(name => name, ICheck (name) => new NamedCheck(name, ran)),
            capabilities: new() { Features = new HashSet<string> { "structuredOutput" } });
    }

    /// <summary>Fails when the output contains <c>fail:</c> and its name; it remembers every run.</summary>
    private sealed class NamedCheck(string name, ConcurrentQueue<(string, CheckContext)> ran) : ICheck
    {
        public ValueTask<CheckResult> RunAsync(CheckContext context, CancellationToken ct)
        {
            ran.Enqueue((name, context));
            var failed = context.Output!.Contains($"fail:{name}", StringComparison.Ordinal);
            return ValueTask.FromResult(new CheckResult(!failed, failed ? [$"found fail:{name}"] : []));
        }
    }
}
