using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Reports;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>
/// The budget hierarchy above the turn (RUN-05), its warnings (EVT-01), the budget a resumed run has already spent (INV-07),
/// the per-run rate limit (ING-03), and the run report with its cost breakdown (RUN-10, RUN-11). The agent <c>dev</c> is
/// offered <c>read</c>, which takes as long on the clock as a test says, and its model costs $1 per million input tokens.
/// </summary>
public class BudgetTests
{
    private static readonly Usage Thousand = new(1000, 0, 0, 0);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // RUN-05: the agent's budget is between its turns' and the run's, and a turn that reaches it ends in a handoff.
    [Theory]
    [InlineData("agent's cost")]
    [InlineData("agent's tool-call")]
    [InlineData("agent's token")]
    public async Task A_used_up_agent_budget_ends_the_turn_in_a_handoff(string limit)
    {
        var total = limit switch
        {
            "agent's cost" => new TotalBudget { Cost = 0.001m },
            "agent's tool-call" => new TotalBudget { ToolCalls = 1 },
            _ => new TotalBudget { Tokens = 1000 },
        };
        var kit = Kit(agent => agent with { Budget = agent.Budget with { Total = total } });
        kit.Model.Reply(new UsageReported(Thousand), Call(), new Stopped(StopReason.WantsTools));

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.HandedOff, HandoffReason.BudgetExhausted, $"the {limit} budget is used up"), (result.Outcome, result.Handoff!.Reason, result.Handoff.Detail));
        Assert.Single(kit.Model.Requests);
    }

    // RUN-05: the run, like every level, can cap its tokens and tool calls; a run that reaches one ends the turn in a handoff.
    [Theory]
    [InlineData("run's tool-call")]
    [InlineData("run's token")]
    public async Task A_used_up_run_token_or_tool_call_budget_ends_the_turn_in_a_handoff(string limit)
    {
        var kit = Kit(run: limit == "run's token" ? new RunBudget { Tokens = 1000 } : new RunBudget { ToolCalls = 1 });
        kit.Model.Reply(new UsageReported(Thousand), Call(), new Stopped(StopReason.WantsTools));

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.HandedOff, HandoffReason.BudgetExhausted, $"the {limit} budget is used up"), (result.Outcome, result.Handoff!.Reason, result.Handoff.Detail));
        Assert.Single(kit.Model.Requests);
    }

    // EVT-01: a limit that is nearly used up is announced once, before it ends the turn.
    [Fact]
    public async Task A_budget_that_is_nearly_used_up_publishes_one_warning()
    {
        var kit = Kit(agent => agent with { Budget = agent.Budget with { Turn = agent.Budget.Turn with { Cost = 0.00125m } } });
        kit.Model.Reply(new UsageReported(Thousand), Call(), new Stopped(StopReason.WantsTools)).Reply(new UsageReported(Thousand), new TextDelta("Done."), new Stopped(StopReason.Finished));

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        var warnings = (await Events(kit)).Select(coreEvent => coreEvent.Payload).OfType<BudgetWarning>().ToList();
        var warning = Assert.Single(warnings, warning => warning.Limit == "cost");
        Assert.Equal(("turn's", 0.8), (warning.Level, warning.Used));
    }

    // INV-07, RUN-05: a run that resumes has spent what it had spent, so its limits hold across restarts.
    [Theory]
    [InlineData("cost")]
    [InlineData("time")]
    public async Task A_resumed_run_has_already_spent_what_it_spent_before_the_restart(string limit)
    {
        var first = Kit(readTakes: TimeSpan.FromHours(1));
        first.Model.Reply(new UsageReported(Thousand), Call(), new Stopped(StopReason.WantsTools)).Reply(new UsageReported(Thousand), new TextDelta("Done."), new Stopped(StopReason.Finished));
        var work = new Work(Agent, "work");
        await first.Runner.RunAsync(work, Ct);
        await first.Runner.RollbackAsync(work.RunId, 0, ct: Ct);

        // A new process with a run budget that the first one's cost (two calls, $0.002) or time (one hour) already used up.
        var budget = limit == "cost" ? new RunBudget { Cost = 0.002m } : new RunBudget { Time = TimeSpan.FromHours(1) };
        var second = Kit(storage: first.Storage, run: budget);
        second.Model.Reply(new TextDelta("Again."), new Stopped(StopReason.Finished));
        var result = await second.Runner.ResumeAsync(work.RunId, ct: Ct);

        Assert.Equal((AgentOutcome.HandedOff, $"the run's {limit} budget is used up"), (result.Outcome, result.Handoff!.Detail));
        Assert.Empty(second.Model.Requests);

        // With room to spare, the same run goes on.
        var third = Kit(storage: first.Storage);
        await third.Runner.RollbackAsync(work.RunId, 0, ct: Ct);
        third.Model.Reply(new TextDelta("Again."), new Stopped(StopReason.Finished));
        Assert.Equal((AgentOutcome.Completed, "Again."), Outcome(await third.Runner.ResumeAsync(work.RunId, ct: Ct)));
    }

    // INV-07: only the time the turns ran counts, not the days between a crash, a rollback and a resume.
    [Theory]
    [InlineData("rollback")]
    [InlineData("a crash before the first checkpoint")]
    public async Task The_time_a_run_was_down_is_not_counted_against_its_budget(string how)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var crash = how != "rollback";
        var first = Kit(checkpoints: !crash, inRead: async () =>
        {
            arrived.SetResult();
            await release.Task; // the process dies here
        });
        var work = new Work(Agent, "work");
        Task<AgentResult>? dying = null;
        if (crash)
        {
            first.Model.Reply(Call(), new Stopped(StopReason.WantsTools));
            dying = first.Runner.RunAsync(work, Ct);
            await arrived.Task.WaitAsync(Ct);
        }
        else
        {
            first.Model.Reply(new TextDelta("Done."), new Stopped(StopReason.Finished));
            await first.Runner.RunAsync(work, Ct);
        }

        // A new process, three days later, with a run budget of an hour.
        var second = Kit(storage: first.Storage, run: new() { Time = TimeSpan.FromHours(1) });
        second.Time.Advance(TimeSpan.FromDays(3));
        if (!crash)
        {
            await second.Runner.RollbackAsync(work.RunId, 0, ct: Ct);
            second.Time.Advance(TimeSpan.FromDays(3));
        }

        second.Model.Reply(new TextDelta("Again."), new Stopped(StopReason.Finished));

        Assert.Equal((AgentOutcome.Completed, "Again."), Outcome(await second.Runner.ResumeAsync(work.RunId, ct: Ct)));
        release.TrySetResult();
        if (dying is not null)
        {
            await dying;
        }
    }

    // EVT-01: a warning already given is not given again when the run resumes.
    [Fact]
    public async Task A_warning_given_before_a_restart_is_not_given_again()
    {
        var run = new RunBudget { Cost = 0.00125m };
        var first = Kit(run: run);
        first.Model.Reply(new UsageReported(Thousand), Call(), new Stopped(StopReason.WantsTools)).Reply("Done.");
        var work = new Work(Agent, "work");
        await first.Runner.RunAsync(work, Ct);
        await first.Runner.RollbackAsync(work.RunId, 0, ct: Ct);
        var second = Kit(storage: first.Storage, run: run);
        second.Model.Reply(new UsageReported(Thousand), Call(), new Stopped(StopReason.WantsTools));

        await second.Runner.ResumeAsync(work.RunId, ct: Ct);

        var warnings = (await second.Storage.Events.ReadAsync(null, work.RunId, 0, Ct)).Select(coreEvent => coreEvent.Payload).OfType<BudgetWarning>();
        Assert.Single(warnings, warning => warning is { Level: "run's", Limit: "cost" });
    }

    [Fact]
    public async Task The_report_keeps_days_in_the_running_time()
    {
        var kit = Kit();
        kit.Model.Reply("Done.");
        var work = new Work(Agent, "work");
        await kit.Runner.RunAsync(work, Ct);

        var report = await kit.Runner.ReportAsync(work.RunId, ct: Ct);

        Assert.Contains("running 30:00:00", (report with { Running = TimeSpan.FromHours(30) }).ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_cannot_resume_without_the_events_it_reads_its_spending_from()
    {
        var options = Options() with
        {
            Storage = new() { UnstoredEvents = ["modelCallEnded"] },
            Capabilities = new() { ConversationStore = new() { Enabled = true }, Checkpoints = new() { Enabled = true } },
        };

        var error = Assert.Single(options.Validate());

        Assert.Equal("storage.unstoredEvents", error.Path);
        Assert.Contains("modelCallEnded", error.Problem, StringComparison.Ordinal);
    }

    // ING-03: a run takes only so many work items, its first and each resume among them.
    [Fact]
    public async Task The_per_run_rate_limit_rejects_work_beyond_what_a_run_may_take()
    {
        var first = Kit(policies: new() { RateLimits = new() { PerRun = new() { Permits = 1, Window = TimeSpan.FromHours(1) } } });
        first.Model.Reply(new TextDelta("Done."), new Stopped(StopReason.Finished));
        var work = new Work(Agent, "work");
        await first.Runner.RunAsync(work, Ct);
        await first.Runner.RollbackAsync(work.RunId, 0, ct: Ct);

        Assert.Equal((AgentOutcome.Rejected, "the run's rate limit is reached"), Outcome(await first.Runner.ResumeAsync(work.RunId, ct: Ct)));
        first.Model.Reply("Other.");
        Assert.Equal(AgentOutcome.Completed, (await first.Runner.RunAsync(Agent, "other", ct: Ct)).Outcome);
    }

    // RUN-10, RUN-11.
    [Fact]
    public async Task The_report_gives_the_outcome_the_checks_and_the_cost_by_agent_task_step_and_model()
    {
        var kit = Kit(agent => agent with { Output = new() { Checks = ["style"] } });
        kit.Model.Reply(new UsageReported(Thousand), Call(), new Stopped(StopReason.WantsTools)).Reply(new UsageReported(Thousand), new TextDelta("Done."), new Stopped(StopReason.Finished));
        var work = new Work(Agent, "Fix the build.");
        await kit.Runner.RunAsync(work, Ct);

        var report = await kit.Runner.ReportAsync(work.RunId, ct: Ct);

        Assert.Equal((RunStatus.Completed, "Completed"), (report.Status, report.Outcome));
        Assert.Equal(new Spend(0.002m, 2000, 2), report.Cost.Total);
        Assert.Equal([Agent], report.Cost.ByAgent.Keys);
        Assert.Equal(["claude-opus-5-5"], report.Cost.ByModel.Keys);
        Assert.Equal([CostBreakdown.None], report.Cost.ByTask.Keys);
        Assert.Equal([new ReportedCheck("style", null, 1, 0)], report.Checks);
        Assert.Empty(report.OpenIssues);
        var text = report.ToText();
        Assert.Contains("Cost: $0.00 in 2 model calls, 2000 tokens", text, StringComparison.Ordinal);
        Assert.Contains("style: 1 passed, 0 failed", text, StringComparison.Ordinal);
        Assert.Null(await RunReport.BuildAsync(kit.Storage, null, "nobody", Ct));
    }

    // RUN-10.
    [Fact]
    public void Cost_is_broken_down_by_agent_task_step_and_model()
    {
        var time = DateTimeOffset.UnixEpoch;
        CoreEvent Call(string agent, string? step, decimal cost, string model, string? task) =>
            new("run", agent, step, 1, time, new ModelCallEnded(StopReason.Finished, Thousand, cost, model, task));

        var breakdown = CostBreakdown.Of(
        [
            Call("lead", "plan", 0.5m, "big", null), Call("dev", "fix/build", 1m, "big", "t1"), Call("dev", "fix/build", 0.25m, "small", "t1"), Call("dev", null, 2m, "big", "t2"),
            new("run", "dev", null, 5, time, new TurnEnded(AgentOutcome.Completed, null)),
        ]);

        Assert.Equal(new Spend(3.75m, 4000, 4), breakdown.Total);
        Assert.Equal([("dev", 3.25m), ("lead", 0.5m)], breakdown.ByAgent.Select(line => (line.Key, line.Value.Cost)));
        Assert.Equal([("(none)", 0.5m), ("t1", 1.25m), ("t2", 2m)], breakdown.ByTask.Select(line => (line.Key, line.Value.Cost)));
        Assert.Equal([("(none)", 2m), ("fix/build", 1.25m), ("plan", 0.5m)], breakdown.ByStep.Select(line => (line.Key, line.Value.Cost)));
        Assert.Equal([("big", 3.5m), ("small", 0.25m)], breakdown.ByModel.Select(line => (line.Key, line.Value.Cost)));
    }

    [Fact]
    public async Task A_run_that_did_not_end_reports_it_as_open()
    {
        var kit = Kit();
        kit.Model.Reply(new TextDelta("Done."), new Stopped(StopReason.Finished));
        var work = new Work(Agent, "work");
        await kit.Runner.RunAsync(work, Ct);
        await kit.Runner.RollbackAsync(work.RunId, 0, ct: Ct); // the run is running again as far as the store knows

        var report = await kit.Runner.ReportAsync(work.RunId, ct: Ct);

        Assert.Equal(RunStatus.Running, report.Status);
        Assert.Contains("did not end", Assert.Single(report.OpenIssues), StringComparison.Ordinal);
    }

    private static (AgentOutcome, string) Outcome(AgentResult result) => (result.Outcome, result.Output);

    private static ContentReceived Call() => new(new ToolUseContent($"call-{Guid.NewGuid()}", "read", Args("""{ "path": "a.cs" }""")));

    private static async Task<IReadOnlyList<CoreEvent>> Events(TestKit kit) =>
        await kit.Storage.Events.ReadAsync(null, kit.Storage.Runs.Runs[0].RunId, 0, Ct);

    private static TestKit Kit(
        Func<AgentDefinition, AgentDefinition>? configure = null, InMemoryStorage? storage = null, RunBudget? run = null, PolicyOptions? policies = null,
        TimeSpan readTakes = default, bool checkpoints = true, Func<Task>? inRead = null)
    {
        var options = Options(("read", Extension("read")));
        options = options with
        {
            Run = options.Run with { Budget = run ?? new() },
            Policies = policies ?? new(),
            Agents = new Dictionary<string, AgentDefinition>
            {
                // A turn may take longer than its default 45 minutes here, so only the limit a test sets ends it.
                [Agent] = (configure ?? (agent => agent))(options.Agents[Agent] with { Budget = new() { Turn = new() { Time = TimeSpan.FromHours(10) } } }),
            },
            Checks = new Dictionary<string, CheckOptions> { ["style"] = new() { Use = "extension:style" } },
            Providers = new Dictionary<string, ProviderOptions>
            {
                [ProviderOptions.ClaudeName] = ProviderOptions.Claude with { Prices = new Dictionary<string, ModelPrice> { ["claude-opus-5-5"] = new() { Input = 1, Output = 5 } } },
            },
            Capabilities = new() { ConversationStore = new() { Enabled = true }, Checkpoints = new() { Enabled = checkpoints } },
        };
        TestKit? kit = null;
        var tools = new Dictionary<string, ITool>
        {
            ["read"] = new FakeTool(ToolKind.Read, run: async (_, _) =>
            {
                kit!.Time.Advance(readTakes);
                if (inRead is not null)
                {
                    await inRead();
                }

                return ToolResult.Success("contents");
            }),
        };
        kit = new TestKit(options, tools, storage: storage, checks: new Dictionary<string, ICheck> { ["style"] = new PassingCheck() });
        return kit;
    }

    private sealed class PassingCheck : ICheck
    {
        public ValueTask<CheckResult> RunAsync(CheckContext context, CancellationToken ct) => ValueTask.FromResult(new CheckResult(true, []));
    }
}
