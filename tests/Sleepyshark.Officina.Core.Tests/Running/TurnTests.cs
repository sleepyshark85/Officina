using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>
/// One turn: stop reasons (TEST-03), stop conditions, budgets and stalls (TEST-14), and how the turn ends. The agent
/// <c>dev</c> is offered <c>read</c>, whose result is the path it is given, and <c>edit</c>.
/// </summary>
public class TurnTests
{
    private int edits;
    private TestKit? current;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OfficinaOptions Default => Options(
        ("read", Extension("read")), ("edit", Extension("edit") with { GateExemption = "Tests only." }), ("test", Extension("test")));

    // LOOP-03, MSG-05.
    [Theory]
    [InlineData(StopReason.Finished)]
    [InlineData(StopReason.StopSequence)]
    public async Task A_finished_reply_completes_the_turn_with_its_text(StopReason stop)
    {
        var kit = Kit();
        kit.Model.Reply(new TextDelta("The total "), new TextDelta("is 42."), new Stopped(stop));

        var result = await kit.RunAsync(Agent, "Total?", Ct);

        Assert.Equal((AgentOutcome.Completed, "The total is 42."), (result.Outcome, result.Output));
        Assert.Equal([Message.User("Total?"), Message.Assistant("The total is 42.")], result.Transcript);
    }

    // LOOP-03, LOOP-04.
    [Theory]
    [InlineData(StopReason.OutputLimit, HandoffReason.TruncatedOutput)]
    [InlineData(StopReason.Refused, HandoffReason.ProviderRefusal)]
    [InlineData(StopReason.InputTooLong, HandoffReason.ProviderFailure)]
    [InlineData(StopReason.Unknown, HandoffReason.ProviderFailure)]
    [InlineData(StopReason.WantsTools, HandoffReason.ProviderFailure)]
    public async Task Other_stop_reasons_end_in_a_handoff(StopReason stop, HandoffReason reason)
    {
        var kit = Kit();
        kit.Model.Reply(new TextDelta("Partial"), new Stopped(stop));

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.HandedOff, reason, "Partial"), (result.Outcome, result.Handoff!.Reason, result.Handoff.LastText));
    }

    [Fact]
    public async Task A_reply_without_a_stop_reason_has_stopped_for_an_unknown_reason()
    {
        var kit = Kit();
        kit.Model.Reply(new TextDelta("Partial"));

        Assert.Equal(HandoffReason.ProviderFailure, (await kit.RunAsync(Agent, "work", Ct)).Handoff!.Reason);
    }

    [Fact]
    public async Task A_paused_reply_is_continued_by_calling_again()
    {
        var kit = Kit();
        kit.Model.Reply(new TextDelta("Part one. "), new Stopped(StopReason.Paused)).Reply("Part two.");

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.Completed, "Part two."), (result.Outcome, result.Output));
        Assert.Equal(Message.Assistant("Part one. "), kit.Model.Requests[1].History[^1]);
    }

    // LOOP-03, LOOP-10: the tools run, and the next call sees their results.
    [Fact]
    public async Task Tools_the_model_asks_for_run_and_their_results_go_back_to_it()
    {
        var kit = Kit();
        kit.Model.CallTools(("read", """{ "path": "a.cs" }"""), ("read", """{ "path": "b.cs" }""")).Reply("Read both.");

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.Completed, "Read both.", 2), (result.Outcome, result.Output, result.Statistics.ToolCalls));
        Assert.Equal(
            new Message(Role.User, [
                new ToolResultContent("call-1", "<data source=\"tool:read\">\na.cs\n</data>", false),
                new ToolResultContent("call-2", "<data source=\"tool:read\">\nb.cs\n</data>", false)]),
            kit.Model.Requests[1].History[^1]);
        Assert.Equal(["edit", "read", "test"], kit.Model.Requests[0].Tools.Select(tool => tool.Name));
    }

    // MDL-07, MSG-02: a streamed reply is kept in order, and content the core does not read goes back unchanged.
    [Fact]
    public async Task A_streamed_reply_is_sent_back_with_its_reasoning_and_provider_content_unchanged()
    {
        var reasoning = new ReasoningContent("The path is in the task.", "signature");
        var provider = new ProviderContent(Args("""{ "type": "redacted_thinking", "data": "abc" }"""));
        var call = Call("read", """{ "path": "a.cs" }""");
        var kit = Kit();
        kit.Model.Reply(
                new ContentReceived(reasoning), new TextDelta("Let me "), new TextDelta("look."), call, new ContentReceived(provider), new Stopped(StopReason.WantsTools))
            .Reply(new TextDelta("Do"), new TextDelta("ne."), new Stopped(StopReason.Finished));

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal("Done.", result.Output);
        Assert.Equal([reasoning, new TextContent("Let me look."), call.Content, provider], kit.Model.Requests[1].History[1].Content);
    }

    // LOOP-05.
    [Fact]
    public async Task When_finishing_is_not_a_stop_condition_a_finished_reply_ends_in_a_handoff()
    {
        var kit = Kit(agent => agent with { StopWhen = new() { Finished = false, FinishTool = "edit" } });
        kit.Model.Reply("I am done.");

        Assert.Equal(HandoffReason.NoProgress, (await kit.RunAsync(Agent, "work", Ct)).Handoff!.Reason);
    }

    [Fact]
    public async Task A_successful_call_of_the_finish_tool_completes_the_turn_with_its_arguments()
    {
        var kit = Kit(agent => agent with { StopWhen = new() { Finished = false, FinishTool = "edit" } });
        kit.Model.CallTools(("edit", """{"line":"x = 1"}"""));

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.Completed, """{"line":"x = 1"}"""), (result.Outcome, result.Output));
    }

    [Fact]
    public async Task The_turn_completes_when_it_reaches_its_maximum_iterations()
    {
        var kit = Kit(agent => agent with { StopWhen = new() { MaxIterations = 1 } });
        kit.Model.Reply(new TextDelta("Reading."), Call("read", """{ "path": "a.cs" }"""), new Stopped(StopReason.WantsTools));

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.Completed, "Reading.", 1), (result.Outcome, result.Output, result.Statistics.Iterations));
    }

    // LOOP-06, COST-02, TEST-14: every limit of the turn, and the run's, ends the turn in a handoff before the next call.
    [Theory]
    [InlineData("turn's iteration")]
    [InlineData("turn's tool-call")]
    [InlineData("turn's token")]
    [InlineData("turn's cost")]
    [InlineData("turn's time")]
    [InlineData("run's cost")]
    [InlineData("run's time")]
    public async Task A_used_up_budget_ends_the_turn_in_a_handoff_with_everything_recorded(string limit)
    {
        var turn = limit switch
        {
            "turn's iteration" => new TurnBudget { Iterations = 1 },
            "turn's tool-call" => new TurnBudget { ToolCalls = 1 },
            "turn's token" => new TurnBudget { Tokens = 1000 },
            "turn's cost" => new TurnBudget { Cost = 0.001m },
            "turn's time" => new TurnBudget { Time = TimeSpan.FromMinutes(1) },
            _ => new TurnBudget(),
        };
        var run = limit switch
        {
            "run's cost" => new RunBudget { Cost = 0.001m },
            "run's time" => new RunBudget { Time = TimeSpan.FromMinutes(1) },
            _ => new RunBudget(),
        };
        var options = Priced(Default) with { Run = new() { Budget = run } };
        var kit = Kit(agent => agent with { Budget = new() { Turn = turn } }, options, () => current!.Time.Advance(TimeSpan.FromMinutes(1)));
        kit.Model.Reply(
            new TextDelta("Reading."), Call("read", """{ "path": "a.cs" }"""), new UsageReported(new Usage(1000, 0, 0, 0)), new Stopped(StopReason.WantsTools));

        var result = await kit.RunAsync(Agent, "Find the bug.", Ct);

        var handoff = result.Handoff!;
        Assert.Equal((AgentOutcome.HandedOff, HandoffReason.BudgetExhausted, $"the {limit} budget is used up"), (result.Outcome, handoff.Reason, handoff.Detail));
        Assert.Single(kit.Model.Requests);
        Assert.Equal(("Find the bug.", "Reading.", (string?)null), (handoff.Work, handoff.LastText, handoff.To));
        Assert.Equal(("read", "a.cs"), (Assert.Single(handoff.ToolCalls).Request.Name, handoff.ToolCalls[0].Result.Content));
        Assert.Equal(new TurnStatistics(1, 1, new Usage(1000, 0, 0, 0), 0.001m, TimeSpan.FromMinutes(1)), result.Statistics);
        Assert.Equal(3, result.Transcript.Length);
    }

    // MSG-06, MDL-09, CLD-07: the claude provider ships the default model's prices: $4, $20, $0.20, $5 and $8 per million
    // input, output, cache-read, five-minute and one-hour cache-write tokens.
    [Fact]
    public async Task Usage_is_counted_by_kind_and_priced_from_the_shipped_table_with_cache_writes_by_lifetime()
    {
        var kit = Kit();
        kit.Model.Reply(new UsageReported(new Usage(1000, 0, 0, 0)), new UsageReported(new Usage(0, 1000, 0, 0)), new Stopped(StopReason.Paused))
            .Reply(new UsageReported(new Usage(0, 0, 2000, 1000, cacheWrite1h: 400)), new TextDelta("Done."), new Stopped(StopReason.Finished));

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal(new Usage(1000, 1000, 2000, 1000, cacheWrite1h: 400), result.Statistics.Usage);
        Assert.Equal((4m + 20m + (2 * 0.2m) + (0.6m * 5m) + (0.4m * 8m)) / 1000, result.Statistics.Cost);
    }

    // TOOL-13, EVT-01: a provider's own tool is audited and published after the fact, and counts towards the budget.
    [Fact]
    public async Task A_tool_the_provider_ran_is_audited_published_and_counted_towards_the_budget()
    {
        var options = Default with
        {
            Tools = new Dictionary<string, ToolOptions> { ["web_search"] = new() { Source = "provider:web_search", Reason = "Tests only." } },
            ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["all"] = ["web_search"] },
        };
        var kit = Kit(agent => agent with { Budget = new() { Turn = new() { ToolCalls = 1 } } }, options);
        kit.Model.Reply(new ProviderToolUsed(new ToolRequest("web_search", Args("""{ "query": "x" }""")), "3 results"), new Stopped(StopReason.Paused));

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((HandoffReason.BudgetExhausted, 1), (result.Handoff!.Reason, result.Statistics.ToolCalls));
        var entry = Assert.Single(kit.Storage.Audit.Entries);
        Assert.Equal(("web_search", "provider", "3 results"), (entry.Tool, entry.DecidedBy, entry.Detail));
        var events = await kit.Storage.Events.ReadAsync(null, kit.Storage.Runs.Runs.Single().RunId, 0, Ct);
        Assert.Equal(
            [new ToolCallStarted("web_search", """{ "query": "x" }"""), new ToolCallEnded("web_search", null)],
            events.Select(read => read.Payload).Where(payload => payload is ToolCallStarted or ToolCallEnded));
    }

    // MDL-06: a provider tool the agent's provider does not run fails validation.
    [Fact]
    public void A_provider_tool_the_provider_does_not_run_is_rejected()
    {
        var options = Default with
        {
            Tools = new Dictionary<string, ToolOptions> { ["search"] = new() { Source = "provider:web_search", Reason = "Tests only." } },
            ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["all"] = ["search"] },
        };

        var error = Assert.Single(Assert.Throws<ConfigurationException>(() => new TestKit(options)).Errors);

        Assert.Equal(("tools.search.source", "provider \"claude\" does not run the tool \"web_search\"."), (error.Path, error.Problem));
    }

    // LOOP-07, TEST-14.
    [Fact]
    public async Task Repeating_an_identical_call_with_an_identical_result_is_a_stall()
    {
        var kit = Kit();
        for (var i = 0; i < 4; i++)
        {
            kit.Model.CallTools(("read", """{ "path": "a.cs" }"""));
        }

        var result = await kit.RunAsync(Agent, "work", Ct);

        // The first call is new; the three after it make no progress.
        Assert.Equal((HandoffReason.NoProgress, 4), (result.Handoff!.Reason, result.Statistics.Iterations));
    }

    [Fact]
    public async Task Reading_a_series_of_different_files_is_not_a_stall()
    {
        var kit = Kit();
        foreach (var file in new[] { "a.cs", "b.cs", "c.cs", "d.cs", "e.cs" })
        {
            kit.Model.CallTools(("read", $$"""{ "path": "{{file}}" }"""));
        }

        kit.Model.Reply("Found it.");

        Assert.Equal(AgentOutcome.Completed, (await kit.RunAsync(Agent, "work", Ct)).Outcome);
    }

    [Fact]
    public async Task An_edit_test_edit_cycle_is_not_a_stall_even_when_an_edit_repeats()
    {
        var kit = Kit();
        foreach (var line in new[] { "x = 1", "x = 2", "x = 1", "x = 2" })
        {
            kit.Model.CallTools(("edit", $$"""{ "line": "{{line}}" }""")).CallTools(("test", "{}"));
        }

        kit.Model.Reply("Fixed.");

        Assert.Equal(AgentOutcome.Completed, (await kit.RunAsync(Agent, "work", Ct)).Outcome);
    }

    [Fact]
    public async Task The_number_of_iterations_without_progress_is_configurable()
    {
        var kit = Kit(agent => agent with { Stall = new() { IterationsWithoutProgress = 1 } });
        kit.Model.CallTools(("read", """{ "path": "a.cs" }""")).CallTools(("read", """{ "path": "a.cs" }"""));

        Assert.Equal((HandoffReason.NoProgress, 2), ((await kit.RunAsync(Agent, "work", Ct)).Handoff!.Reason, kit.Model.Requests.Count));
    }

    // LOOP-11.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task When_every_call_of_an_iteration_is_refused_the_turn_ends_in_a_policy_gap_unless_configured_not_to(bool handOff)
    {
        var options = Default with { Tools = new Dictionary<string, ToolOptions>(Default.Tools) { ["read"] = Extension("read") with { Permissions = ["files:read"] } } };
        var kit = Kit(agent => agent with { HandOffOnPolicyGap = handOff }, options);
        kit.Model.CallTools(("read", """{ "path": "a.cs" }"""), ("read", """{ "path": "b.cs" }""")).Reply("I may not read.");

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal(handOff ? HandoffReason.PolicyGap : (HandoffReason?)null, result.Handoff?.Reason);
        Assert.Equal(handOff ? AgentOutcome.HandedOff : AgentOutcome.Completed, result.Outcome);
    }

    // EGR-02, EGR-04: a gate's route hands the turn to whom it names, with the call pending.
    [Fact]
    public async Task A_routed_call_ends_the_turn_in_a_handoff_to_where_it_was_routed()
    {
        var options = Default with
        {
            Agents = new Dictionary<string, AgentDefinition>(Default.Agents) { ["lead"] = new() { Instructions = "Lead." } },
            Policies = new() { PermissionRules = [new() { Tool = "edit", Action = PolicyAction.Route, To = "lead", Reason = "edits need the lead" }] },
        };
        var kit = Kit(options: options);
        kit.Model.CallTools(("edit", """{ "line": "x = 1" }"""));

        var handoff = (await kit.RunAsync(Agent, "work", Ct)).Handoff!;

        Assert.Equal((HandoffReason.RoutedByGate, "lead", "edit"), (handoff.Reason, handoff.To, handoff.PendingAction!.Name));
        Assert.Equal(0, edits);
    }

    // CLD-06, COST-02: the tool calls of a model whose reply was declined, which the provider withdraws, are not run, and the
    // fallback that took over without a price is warned of.
    [Fact]
    public async Task Withdrawn_tool_calls_are_not_run_and_an_unpriced_fallback_is_warned_of()
    {
        var kit = Kit(options: Priced(Default));
        var fallback = new ProviderContent(Args("""{ "type": "fallback" }"""));
        kit.Model.Reply(
            Call("edit", """{"line":"x = 1"}"""), new ToolCallsWithdrawn(), new ContentReceived(fallback),
            new FallbackUsed("other", new ModelProfile { Model = "claude-unpriced" }, ModelFailure.Refused), new TextDelta("Done."), new Stopped(StopReason.Finished));
        var work = new Work(Agent, "work");

        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal((AgentOutcome.Completed, "Done.", 0), (result.Outcome, result.Output, edits));
        Assert.Equal([fallback, new TextContent("Done.")], result.Transcript[^1].Content);
        var warning = Assert.Single((await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct)).Select(read => read.Payload).OfType<Warning>());
        Assert.StartsWith("Model claude-unpriced has no price", warning.Text, StringComparison.Ordinal);
    }

    // EGR-04, INV-01: words in the model's text decide nothing.
    [Fact]
    public async Task Asking_for_a_human_in_text_is_not_a_handoff()
    {
        var kit = Kit();
        kit.Model.Reply("I am not confident. Please hand this off to a human.");

        Assert.Equal(AgentOutcome.Completed, (await kit.RunAsync(Agent, "work", Ct)).Outcome);
    }

    private static ContentReceived Call(string tool, string arguments) => new(new ToolUseContent("call-0", tool, Args(arguments)));

    /// <summary>The default model at $1 and $5 per million input and output tokens.</summary>
    private static OfficinaOptions Priced(OfficinaOptions options) => options with
    {
        Providers = new Dictionary<string, ProviderOptions>
        {
            [ProviderOptions.ClaudeName] = ProviderOptions.Claude with
            {
                Prices = new Dictionary<string, ModelPrice> { ["claude-opus-5-5"] = new() { Input = 1, Output = 5 } },
            },
        },
    };

    /// <param name="configure">Changes to the agent <c>dev</c>.</param>
    /// <param name="options">The configuration; <see cref="Default"/> when omitted.</param>
    /// <param name="onRead">What else a call of <c>read</c> does.</param>
    private TestKit Kit(Func<AgentDefinition, AgentDefinition>? configure = null, OfficinaOptions? options = null, Action? onRead = null)
    {
        options ??= Default;
        var agents = new Dictionary<string, AgentDefinition>(options.Agents) { [Agent] = (configure ?? (agent => agent))(options.Agents[Agent]) };
        var tools = new Dictionary<string, ITool>
        {
            ["read"] = new FakeTool(ToolKind.Read, parallelSafe: true, run: (call, _) =>
            {
                onRead?.Invoke();
                return ValueTask.FromResult(ToolResult.Success(call.Arguments.GetProperty("path").GetString()!));
            }),
            ["edit"] = new FakeTool(ToolKind.Write, run: (_, _) =>
            {
                edits++;
                return ValueTask.FromResult(ToolResult.Success("applied"));
            }),
            ["test"] = new FakeTool(ToolKind.Read, run: (_, _) => ValueTask.FromResult(ToolResult.Success($"tests ran after {edits} edits"))),
        };
        current = new TestKit(options with { Agents = agents }, tools, capabilities: new() { ProviderTools = new HashSet<string> { "web_search" } });
        return current;
    }
}
