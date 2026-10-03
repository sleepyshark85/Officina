using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using Sleepyshark.Officina.Core.Tests.Tools;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>
/// Loop patterns built from turns (PAT). The agent <c>lead</c> has the pattern under test, and its steps are turns of
/// <c>worker</c>, which is offered <c>report</c>: a tool whose result carries the artifact <c>contact.txt</c>, holding an
/// email address. The check <c>fixed</c> passes only the output <c>fixed</c>, and its findings quote the artifacts.
/// The patterns' samples run in the CLI's tests.
/// </summary>
public class PatternTests
{
    private Func<CancellationToken, Task<ToolResult>> report = _ => Task.FromResult(ToolResult.Success("written", new Artifact("contact.txt", "jane@example.com")));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // PAT-06: every step's turn draws on the pattern's budget, which is the agent's turn budget.
    [Fact]
    public async Task Steps_draw_on_the_budget_of_their_pattern()
    {
        var kit = Kit(Workflow("one", "two", "three"), lead => lead with { Budget = new() { Turn = new() { Iterations = 2 } } });
        kit.Model.Reply("1").Reply("2");

        var result = await kit.RunAsync("lead", "work", Ct);

        Assert.Equal(
            (HandoffReason.BudgetExhausted, "the pattern's iteration budget is used up", 2),
            (result.Handoff!.Reason, result.Handoff.Detail, result.Statistics.Iterations));
    }

    // RUN-05: a step agent's own budget.total covers all its steps in the run, beside the pattern's budget it draws on.
    [Fact]
    public async Task A_step_agents_own_budget_covers_all_its_steps()
    {
        var kit = Kit(Workflow("one", "two"), worker: worker => worker with { Budget = new() { Total = new() { ToolCalls = 2 } } });
        kit.Model.CallTools(("report", "{}")).Reply("1").CallTools(("report", "{}")).Reply("never asked for");

        var result = await kit.RunAsync("lead", "work", Ct);

        Assert.Equal((HandoffReason.BudgetExhausted, "the agent's tool-call budget is used up"), (result.Handoff!.Reason, result.Handoff.Detail));
        Assert.Equal(3, kit.Model.Requests.Count);
    }

    // PAT-04, PAT-08: a step that is handed off is retried; another takes a different branch, and a step gets the inputs it declares.
    [Fact]
    public async Task A_workflow_step_is_retried_or_goes_elsewhere_by_its_outcome()
    {
        var pattern = Workflow("one", "two", "three", "four");
        pattern = pattern with
        {
            Steps =
            [
                pattern.Steps[0] with { OnOutcome = new() { HandedOff = "retry:1" } },
                pattern.Steps[1] with { OnOutcome = new() { HandedOff = "goto:four" } },
                pattern.Steps[2],
                pattern.Steps[3] with { Input = ["one"] },
            ],
        };
        var kit = Kit(pattern);
        kit.Model.Reply(new Stopped(StopReason.Refused)).Reply("first").Reply(new Stopped(StopReason.Refused)).Reply("last");

        var result = await kit.RunAsync("lead", "work", Ct);

        Assert.Equal((AgentOutcome.Completed, "last"), (result.Outcome, result.Output));
        Assert.Equal(Message.User("first"), kit.Model.Requests[^1].History[^1]);
        Assert.Equal(
            [("one", StepOutcome.HandedOff), ("one", StepOutcome.Completed), ("two", StepOutcome.HandedOff), ("four", StepOutcome.Completed)],
            (await StepsEnded(kit)).Select(ended => (ended.Step!, ((StepEnded)ended.Payload).Outcome)));
    }

    // PAT-05: the same step over each item of a list in the input, at most maxParallel at once, all results collected.
    [Fact]
    public async Task Fan_out_runs_a_branch_on_each_item_and_collects_every_output()
    {
        var kit = Kit(new() { Type = PatternOptions.FanOut, Over = "input.files", Branches = [new() { Agent = "worker" }], MaxParallel = 1 });
        kit.Model.Reply("A is fine.").Reply("B is fine.");

        var result = await kit.RunAsync("lead", """{ "files": ["a.cs", "b.cs"] }""", Ct);

        Assert.Equal("""["A is fine.","B is fine."]""", result.Output);
        Assert.Equal(Message.User("b.cs"), kit.Model.Requests[1].History[^1]);
    }

    [Fact]
    public async Task Fan_out_to_the_first_success_stops_the_other_branches()
    {
        var kit = Kit(new()
        {
            Type = PatternOptions.FanOut, Branches = [new() { Agent = "worker" }, new() { Agent = "worker" }], Combine = FanOutCombine.FirstSuccess, MaxParallel = 1,
        });
        kit.Model.Reply("A");

        var result = await kit.RunAsync("lead", "work", Ct);

        Assert.Equal((AgentOutcome.Completed, "A", 1), (result.Outcome, result.Output, kit.Model.Requests.Count));
    }

    [Fact]
    public async Task Fan_out_combines_the_outputs_in_a_step()
    {
        var kit = Kit(new() { Type = PatternOptions.FanOut, Branches = [new() { Agent = "worker" }, new() { Agent = "worker" }], Combine = FanOutCombine.Step, Combiner = new() { Agent = "worker" }, MaxParallel = 1 });
        kit.Model.Reply("A").Reply("B").Reply("A and B");

        var result = await kit.RunAsync("lead", "work", Ct);

        Assert.Equal("A and B", result.Output);
        Assert.Equal(
            "<data source=\"step:branch[0]\">\nA\n</data>\n<data source=\"step:branch[1]\">\nB\n</data>",
            kit.Model.Requests[^1].History[^1].Content.OfType<TextContent>().Single().Text);
    }

    [Fact]
    public async Task Fan_out_with_no_majority_is_handed_off()
    {
        var kit = Kit(new() { Type = PatternOptions.FanOut, Branches = [new() { Agent = "worker" }, new() { Agent = "worker" }], Combine = FanOutCombine.Majority, On = "output.v", MaxParallel = 1 },
            worker: agent => agent with { Output = new() { Format = OutputFormat.Structured, Schema = """{ "properties": { "v": { "type": "string" } } }""", Checks = [] } });
        kit.Model.Reply("""{ "v": "a" }""").Reply("""{ "v": "b" }""");

        var result = await kit.RunAsync("lead", "work", Ct);

        Assert.Equal(HandoffReason.NoRouteForValue, result.Handoff!.Reason);
    }

    // PAT-01: when an item is handed off, the planner is asked again, told which item failed, and only the new plan's outputs count.
    [Fact]
    public async Task Plan_and_execute_plans_again_when_an_item_is_handed_off()
    {
        var kit = PlanKit(maxReplans: 1);
        kit.Model.Reply("""{ "steps": ["a", "b"] }""").Reply("done a").Reply(new Stopped(StopReason.Refused)).Reply("""{ "steps": ["c"] }""").Reply("done c");

        var result = await kit.RunAsync("lead", "work", Ct);

        Assert.Equal((AgentOutcome.Completed, """["done c"]"""), (result.Outcome, result.Output));
        Assert.Contains("executor[1] did not complete", Text(kit.Model.Requests[3]), StringComparison.Ordinal);
    }

    // PAT-01, PAT-04: the planner is told it only plans; each item gets the work, the plan and the earlier items' outputs, labelled.
    [Fact]
    public async Task Plan_and_execute_gives_each_item_the_work_the_plan_and_the_earlier_outputs()
    {
        var kit = PlanKit(maxReplans: 0);
        kit.Model.Reply("""{ "steps": ["parse", "print"] }""").Reply("parsed 1 and 2").Reply("3");

        var result = await kit.RunAsync("lead", "Build a calculator.", Ct);

        Assert.Equal((AgentOutcome.Completed, """["parsed 1 and 2","3"]"""), (result.Outcome, result.Output));
        Assert.Equal(
            "Write the plan for the work below, and do not do the work yourself: short steps, in order, that a worker carries out one at a time. "
            + "Each step's worker sees the work, the plan and the earlier steps' results.\n\nBuild a calculator.",
            ScriptedModelProvider.WorkOf(kit.Model.Requests[0]));
        Assert.Equal(
            """
            <data source="input">
            Build a calculator.
            </data>
            <data source="step:planner">
            { "steps": ["parse", "print"] }
            </data>
            <data source="step:executor[0]">
            parsed 1 and 2
            </data>

            Your step (2 of 2):
            print
            """.ReplaceLineEndings("\n"),
            ScriptedModelProvider.WorkOf(kit.Model.Requests[2]));
    }

    [Fact]
    public async Task Plan_and_execute_stops_planning_again_at_the_limit()
    {
        var kit = PlanKit(maxReplans: 1);
        kit.Model.Reply("""{ "steps": ["a"] }""").Reply(new Stopped(StopReason.Refused)).Reply("""{ "steps": ["b"] }""").Reply(new Stopped(StopReason.Refused));

        var result = await kit.RunAsync("lead", "work", Ct);

        Assert.Equal(AgentOutcome.HandedOff, result.Outcome);
        Assert.Equal(4, kit.Model.Requests.Count);
    }

    private TestKit PlanKit(int maxReplans) =>
        Kit(new() { Type = PatternOptions.PlanAndExecute, Executor = new() { Agent = "worker" }, MaxReplans = maxReplans },
            lead => lead with { Output = new() { Format = OutputFormat.Structured, Schema = """{ "properties": { "steps": { "type": "array" } } }""" } });

    // OUT-03, ING-02: the findings go back to the model masked, though the check saw the artifact as the tool produced it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_checks_are_revised_with_their_findings_masked(bool evaluateAndRevise)
    {
        var kit = evaluateAndRevise
            ? Kit(new() { Type = PatternOptions.EvaluateAndRevise, Generate = new() { Agent = "worker" }, Checks = ["fixed"] })
            : Kit(new(), lead => lead with { Tools = ["all"], Output = new() { Checks = ["fixed"], OnCheckFailure = CheckFailure.Revise } });
        kit.Model.CallTools(("report", "{}")).Reply("draft").Reply("fixed");

        var result = await kit.RunAsync("lead", "work", Ct);

        Assert.Equal((AgentOutcome.Completed, "fixed"), (result.Outcome, result.Output));
        Assert.Contains("check fixed failed: contact.txt holds [email-1]", Text(kit.Model.Requests[^1]), StringComparison.Ordinal);
        Assert.DoesNotContain(kit.Model.Requests, request => Text(request).Contains("jane@example.com", StringComparison.Ordinal));
    }

    // PAT-07.
    [Fact]
    public async Task An_application_pattern_runs_by_name_through_the_core()
    {
        var kit = Kit(new() { Type = "extension:Twice", Generate = new() { Agent = "worker" } }, patterns: new Dictionary<string, ILoopPattern> { ["Twice"] = new Twice() });
        kit.Model.Reply("draft").Reply("final");

        var result = await kit.RunAsync("lead", "work", Ct);

        Assert.Equal((AgentOutcome.Completed, "final", 2), (result.Outcome, result.Output, result.Statistics.Iterations));
        Assert.Equal(Message.User("draft"), kit.Model.Requests[1].History[^1]);
        Assert.Equal(["first", "second"], (await StepsEnded(kit)).Select(ended => ended.Step));
    }

    [Fact]
    public void An_application_pattern_that_is_not_registered_is_reported()
    {
        var error = Assert.Throws<ConfigurationException>(() => Kit(new() { Type = "extension:Twice", Generate = new() { Agent = "worker" } }));

        Assert.Equal(("agents.lead.pattern.type", "pattern extension \"Twice\" is not registered."), (Assert.Single(error.Errors).Path, error.Errors[0].Problem));
    }

    // PAT-06: cancelling the run cancels the step, and the run ends in a handoff.
    [Fact]
    public async Task Cancelling_a_step_hands_the_run_off()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        report = async token =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return ToolResult.Success("never");
        };
        var kit = Kit(Workflow("one", "two"));
        kit.Model.CallTools(("report", "{}"));

        var result = await kit.RunAsync("lead", "work", cancel.Token);

        Assert.Equal((AgentOutcome.HandedOff, HandoffReason.RequestedByHuman), (result.Outcome, result.Handoff!.Reason));
        Assert.Equal(StepOutcome.Cancelled, ((StepEnded)Assert.Single(await StepsEnded(kit)).Payload).Outcome);
    }

    // PAT-02, PAT-03, CFG-13: patterns are checked before anything runs.
    [Theory]
    [InlineData("unknown type", "agents.lead.pattern.type", "\"loop\" is not a pattern.")]
    [InlineData("router without on", "agents.lead.pattern.on", "is required for the router pattern.")]
    [InlineData("router on text", "agents.lead.pattern.on", "reads the output of a step that is not a turn with structured output.")]
    [InlineData("route not in schema", "agents.lead.pattern.on", "field output.desk is not in the step's output.")]
    [InlineData("missing goto", "agents.lead.pattern.next[0].goto", "step \"nowhere\" does not exist.")]
    [InlineData("loop of agents", "agents.lead.pattern", "has the agent as a step of its own pattern.")]
    [InlineData("nested turn", "agents.lead.pattern.steps[0].pattern", "\"singleCall\" is a turn, not a nested pattern.")]
    [InlineData("single call with tools", "agents.lead.tools", "must be empty: a singleCall agent is offered no tools.")]
    public void Invalid_patterns_are_reported(string kind, string path, string problem)
    {
        var structured = new OutputOptions { Format = OutputFormat.Structured, Schema = """{ "properties": { "route": { "type": "string" } } }""" };
        var (pattern, lead) = kind switch
        {
            "unknown type" => (new PatternOptions { Type = "loop" }, (Func<AgentDefinition, AgentDefinition>?)null),
            "router without on" => (new() { Type = PatternOptions.Router, Routes = Routes }, null),
            "router on text" => (new() { Type = PatternOptions.Router, Routes = Routes, On = "output.route" }, null),
            "route not in schema" => (new() { Type = PatternOptions.Router, Routes = Routes, On = "output.desk" }, agent => agent with { Output = structured }),
            "missing goto" => (Workflow("one") with { Next = [new() { From = "one", Goto = "nowhere" }] }, null),
            "nested turn" => (new() { Type = PatternOptions.Workflow, Steps = [new() { Id = "one", Pattern = new() { Type = PatternOptions.SingleCall } }] }, null),
            "loop of agents" => (new() { Type = PatternOptions.Router, Classify = new() { Agent = "lead" }, Routes = Routes, On = "output.route" }, null),
            _ => (new() { Type = PatternOptions.SingleCall }, agent => agent with { Tools = ["all"] }),
        };

        var errors = Options(pattern, lead).Validate();

        Assert.Contains(errors, error => (error.Path, error.Problem) == (path, problem));
    }

    private static Dictionary<string, StepOptions> Routes => new() { ["work"] = new() { Agent = "worker" } };

    private static PatternOptions Workflow(params string[] ids) =>
        new() { Type = PatternOptions.Workflow, Steps = [.. ids.Select(id => new StepOptions { Id = id, Agent = "worker" })] };

    private static OfficinaOptions Options(PatternOptions pattern, Func<AgentDefinition, AgentDefinition>? lead = null, Func<AgentDefinition, AgentDefinition>? worker = null)
    {
        var options = ToolSetup.Options(("report", Extension("report")));
        var workerAgent = (worker ?? (agent => agent))(options.Agents[Agent] with { Output = new() { Checks = [] } });
        return options with
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["lead"] = (lead ?? (agent => agent))(new() { Instructions = "Lead.", Pattern = pattern }),
                ["worker"] = workerAgent,
            },
            Checks = new Dictionary<string, CheckOptions> { ["fixed"] = new() { Use = "extension:fixed" } },
        };
    }

    private static string Text(ModelRequest request) =>
        string.Join('\n', request.History.SelectMany(message => message.Content).Select(content => content switch
        {
            TextContent text => text.Text,
            ToolResultContent result => result.Text,
            _ => "",
        }));

    private static async Task<IEnumerable<CoreEvent>> StepsEnded(TestKit kit) =>
        (await kit.Storage.Events.ReadAsync(null, kit.Storage.Runs.Runs[^1].RunId, 0, Ct)).Where(coreEvent => coreEvent.Payload is StepEnded);

    private TestKit Kit(PatternOptions pattern, Func<AgentDefinition, AgentDefinition>? lead = null, IReadOnlyDictionary<string, ILoopPattern>? patterns = null, Func<AgentDefinition, AgentDefinition>? worker = null) =>
        new(
            Options(pattern, lead, worker),
            new Dictionary<string, ITool> { ["report"] = new FakeTool(ToolKind.Read, run: async (_, token) => await report(token)) },
            checks: new Dictionary<string, ICheck> { ["fixed"] = new Fixed() },
            patterns: patterns);

    private sealed class Fixed : ICheck
    {
        public ValueTask<CheckResult> RunAsync(CheckContext context, CancellationToken ct) =>
            ValueTask.FromResult(new CheckResult(context.Output == "fixed", [.. context.Artifacts.Select(artifact => $"{artifact.Name} holds {artifact.Content}")]));
    }

    /// <summary>Runs its <c>generate</c> step, then the same step again on the first one's output.</summary>
    private sealed class Twice : ILoopPattern
    {
        public async ValueTask<StepResult> RunAsync(PatternContext context, CancellationToken ct)
        {
            var first = await context.RunStepAsync("first", context.Pattern.Generate!, context.Input, ct);
            return first.Outcome == StepOutcome.Completed ? await context.RunStepAsync("second", context.Pattern.Generate!, first.Output, ct) : first;
        }
    }
}
