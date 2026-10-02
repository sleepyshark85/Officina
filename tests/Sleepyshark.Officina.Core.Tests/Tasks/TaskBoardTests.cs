using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tasks;
using Sleepyshark.Officina.Core.Tests.Tools;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Tasks;

/// <summary>
/// The task board (TASK). The agents <c>dev</c> and <c>reviewer</c> of one run act on it through the task tools and the
/// real tool pipeline; the owner through the pipeline's board. The check <c>tests</c> stands in for a test command, and
/// each test says whether it passes. Two failed attempts send a task back to the lead.
/// </summary>
public class TaskBoardTests
{
    private const string Reviewer = "reviewer";

    private readonly ToolSetup setup = new();
    private readonly ToolPipeline pipeline;
    private readonly TaskBoard owner;
    private readonly OfficinaOptions options;
    private bool testsPass = true;

    public TaskBoardTests()
    {
        setup.Checks["tests"] = new TestCommand(() => testsPass);
        setup.Gates["in-progress"] = new TaskInProgress();
        var options = Configure(Options(
            ("create", new() { Source = "builtin:tasks.create" }),
            ("update", new() { Source = "builtin:tasks.update" }),
            ("claim", new() { Source = "builtin:tasks.claim" }),
            ("submit", new() { Source = "builtin:tasks.submit_for_review" }),
            ("review", new() { Source = "builtin:tasks.review" }),
            ("edit", Extension("edit") with { Gates = ["in-progress"] })));
        options = options with
        {
            Agents = new Dictionary<string, AgentDefinition>(options.Agents) { [Reviewer] = options.Agents[Agent] },
            Gates = new Dictionary<string, GateOptions> { ["in-progress"] = new() { Use = "extension:in-progress" } },
        };
        setup.Tools["edit"] = new FakeTool(ToolKind.Write);
        this.options = options;
        pipeline = setup.Create(options);
        owner = pipeline.Board(Owner.Tenant, Context.RunId);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // TASK-05, INV-09, TEST-20.
    [Fact]
    public async Task A_task_reaches_done_only_once_its_checks_pass_whatever_the_agent_says()
    {
        await AddAsync("t1", new() { Title = "Fix the parser", Checks = ["tests"] });
        await CallAsync(Agent, "claim", """{ "id": "t1" }""");
        testsPass = false;

        var failed = await CallAsync(Agent, "submit", """{ "id": "t1" }""");
        var claimed = await CallAsync(Agent, "update", """{ "id": "t1", "state": "done", "reason": "The tests pass." }""");
        var completed = await owner.CompleteAsync("t1", "integrated", Ct);

        Assert.Equal("check tests failed: 2 tests fail. Changed: t1 failedAttempts 0 → 1.", failed);
        Assert.StartsWith("invalid arguments: ", claimed, StringComparison.Ordinal);
        Assert.Equal((false, "task t1 cannot go from InProgress to Done."), completed);
        testsPass = true;
        Assert.Equal("Changed: t1 state InProgress → InReview, verified false → true.", await CallAsync(Agent, "submit", """{ "id": "t1" }"""));
        Assert.True((await owner.CompleteAsync("t1", "integrated", Ct)).Accepted);
        Assert.Equal(TaskState.Done, (await TaskAsync("t1")).State);
    }

    // TASK-06, TASK-07, TEST-20.
    [Fact]
    public async Task The_reviewer_is_never_the_author_and_every_change_is_recorded_with_who_when_what_and_why()
    {
        await AddAsync("t1", new() { Title = "Fix the parser", RequiresReview = true });
        await CallAsync(Agent, "claim", """{ "id": "t1" }""");
        await CallAsync(Agent, "submit", """{ "id": "t1", "artifacts": ["parser.diff"] }""");

        var own = await CallAsync(Agent, "review", """{ "id": "t1", "approved": true, "reasons": "Looks right." }""");
        var unreviewed = await owner.CompleteAsync("t1", "integrated", Ct);
        await CallAsync(Reviewer, "review", """{ "id": "t1", "approved": false, "reasons": "No test for an empty file." }""");
        await CallAsync(Agent, "submit", """{ "id": "t1" }""");
        await CallAsync(Reviewer, "review", """{ "id": "t1", "approved": true, "reasons": "Covered now." }""");
        await owner.CompleteAsync("t1", "integrated", Ct);

        Assert.Equal("invalid arguments: the reviewer is never the author.", own);
        Assert.Equal((false, "task t1 cannot be done until its checks pass and a reviewer approves it."), unreviewed);
        Assert.Equal(
            [
                ("owner", "t1 added as Ready", "planned"),
                ("dev", "t1 assignee null → dev, state Ready → InProgress", "claimed"),
                ("dev", "t1 state InProgress → InReview, verified false → true, artifacts [] → [\"parser.diff\"]", "its checks passed"),
                ("reviewer", "t1 state InReview → InProgress, verified true → false, failedAttempts 0 → 1", "changes asked: No test for an empty file."),
                ("dev", "t1 state InProgress → InReview, verified false → true, artifacts [\"parser.diff\"] → []", "its checks passed"),
                ("reviewer", "t1 approved false → true", "approved: Covered now."),
                ("owner", "t1 state InReview → Done", "integrated"),
            ],
            (await owner.HistoryAsync(Ct)).Select(change => (change.By, change.What, change.Reason)));
        Assert.All(await owner.HistoryAsync(Ct), change => Assert.Equal(setup.Time.GetUtcNow(), change.Time));
    }

    // TASK-03, TEST-21; EVT-01: each status change is published.
    [Fact]
    public async Task Tasks_never_start_before_their_dependencies()
    {
        await CallAsync(Agent, "create", """{ "id": "t1", "title": "Add the model", "reason": "plan" }""");
        await CallAsync(Agent, "create", """{ "id": "t2", "title": "Add the API", "dependsOn": ["t1"], "reason": "plan" }""");

        var early = await CallAsync(Agent, "claim", """{ "id": "t2" }""");
        await CallAsync(Agent, "claim", """{ "id": "t1" }""");
        await CallAsync(Agent, "submit", """{ "id": "t1" }""");
        await owner.CompleteAsync("t1", "integrated", Ct);

        Assert.Equal("invalid arguments: task t2 is Proposed; only a Ready task, whose dependencies are done, can be claimed.", early);
        Assert.Equal("Changed: t2 assignee null → dev, state Ready → InProgress.", await CallAsync(Agent, "claim", """{ "id": "t2" }"""));
        Assert.Equal(
            [("t1", TaskState.Ready), ("t2", TaskState.Proposed), ("t1", TaskState.InProgress), ("t1", TaskState.InReview), ("t1", TaskState.Done),
                ("t2", TaskState.Ready), ("t2", TaskState.InProgress)],
            (await setup.Events.ReadAsync(Owner.Tenant, Context.RunId, 0, Ct)).Select(read => read.Payload).OfType<TaskStatusChanged>()
                .Select(changed => (changed.Task, changed.Status)));
    }

    // TASK-03, TEST-21.
    [Theory]
    [InlineData("create", """{ "id": "t3", "title": "Loop", "dependsOn": ["t3"], "reason": "plan" }""", "the dependencies would form a cycle: t3 → t3.")]
    [InlineData("update", """{ "id": "t1", "dependsOn": ["t2"], "reason": "plan" }""", "the dependencies would form a cycle: t1 → t2 → t1.")]
    [InlineData("create", """{ "id": "t3", "title": "Late", "dependsOn": ["t9"], "reason": "plan" }""", "task t3 depends on t9, which is not on the board.")]
    public async Task Circular_and_unknown_dependencies_are_rejected(string tool, string arguments, string problem)
    {
        await AddAsync("t1", new() { Title = "Add the model" });
        await AddAsync("t2", new() { Title = "Add the API", DependsOn = ["t1"] });

        Assert.Equal($"invalid arguments: {problem}", await CallAsync(Agent, tool, arguments));
        Assert.Equal(2, (await owner.HistoryAsync(Ct)).Count);
    }

    // TASK-08, INV-10: the owner changes anything at any time; agents cannot change the checks that verify their work.
    [Fact]
    public async Task The_owner_can_edit_reprioritise_reassign_and_cancel_tasks_at_any_time()
    {
        await AddAsync("t1", new() { Title = "Fix the parser", Checks = ["tests"] });
        await AddAsync("t2", new() { Title = "Write the docs" });
        await CallAsync(Agent, "claim", """{ "id": "t1" }""");

        var agentEdit = await CallAsync(Agent, "update", """{ "id": "t1", "checks": [], "reason": "The tests are slow." }""");
        await owner.EditAsync("t1", new() { Priority = 5, Assignee = Reviewer, Description = "Empty files crash it." }, "reassigned", Ct);
        await owner.EditAsync("t2", new() { State = TaskState.Cancelled }, "not needed", Ct);

        Assert.StartsWith("invalid arguments: ", agentEdit, StringComparison.Ordinal);
        Assert.Equal(
            [("t1", 5, Reviewer, TaskState.InProgress, "Empty files crash it."), ("t2", 0, null, TaskState.Cancelled, "")],
            (await owner.ReadAsync(Ct)).Select(task => (task.Id, task.Priority, task.Assignee, task.State, task.Description)));
        Assert.Equal("invalid arguments: task t1 is not in progress with dev.", await CallAsync(Agent, "submit", """{ "id": "t1" }"""));
        Assert.Equal("Changed: t1 state InProgress → InReview, verified false → true.", await CallAsync(Reviewer, "submit", """{ "id": "t1" }"""));
    }

    // TEAM-02, TEAM-09, TASK-08: an agent changes only a task nobody has claimed, and of its own claimed task only whether it is
    // blocked; the lead, by the authority the team gives it and not by its name, also assigns, retries and cancels.
    [Fact]
    public async Task Agents_change_only_unclaimed_tasks_and_the_lead_also_assigns_retries_and_cancels()
    {
        await AddAsync("t1", new() { Title = "Fix the parser", AcceptanceCriteria = ["Empty files parse."] });
        await AddAsync("t2", new() { Title = "Write the docs" });
        await AddAsync("t3", new() { Title = "Release it" });
        await CallAsync(Agent, "claim", """{ "id": "t1" }""");

        Assert.Equal(
            "invalid arguments: only the lead or the owner changes a task once it is claimed; you can block or unblock your own.",
            await CallAsync(Agent, "update", """{ "id": "t1", "acceptanceCriteria": ["It compiles."], "reason": "Easier." }"""));
        Assert.StartsWith("Changed: t1 state InProgress → Blocked", await CallAsync(Agent, "update", """{ "id": "t1", "state": "blocked", "reason": "Waiting for the grammar." }"""), StringComparison.Ordinal);
        Assert.Equal(
            "invalid arguments: task t1 is Blocked with dev; only the lead or the owner changes it now.",
            await CallAsync(Reviewer, "update", """{ "id": "t1", "title": "Mine now", "reason": "Faster." }"""));
        Assert.Equal(
            "invalid arguments: only the lead or the owner assigns or cancels a task.",
            await CallAsync(Agent, "update", """{ "id": "t2", "assignee": "reviewer", "reason": "Split the work." }"""));
        Assert.StartsWith("Changed: t2 priority 0 → 3", await CallAsync(Reviewer, "update", """{ "id": "t2", "priority": 3, "reason": "Urgent." }"""), StringComparison.Ordinal);

        Assert.StartsWith("Changed: t2 assignee null → reviewer", await CallAsync(Reviewer, "update", """{ "id": "t2", "assignee": "reviewer", "reason": "Split the work." }""", lead: true), StringComparison.Ordinal);
        Assert.StartsWith("Changed: t3 state Ready → Cancelled", await CallAsync(Reviewer, "update", """{ "id": "t3", "state": "cancelled", "reason": "Not needed." }""", lead: true), StringComparison.Ordinal);
        Assert.True((await owner.EditAsync("t1", new() { State = TaskState.Ready }, "unblocked", Ct)).Accepted);
        await CallAsync(Agent, "claim", """{ "id": "t1" }""");
        Assert.Equal((true, "Changed: t1 assignee dev → null, state InProgress → Failed."), await owner.FailAsync("t1", "dev stalled", Ct));
        Assert.Equal(
            "invalid arguments: a failed task goes back to the lead; only the lead or the owner retries it.",
            await CallAsync(Agent, "update", """{ "id": "t1", "state": "ready", "reason": "Retry." }"""));
        Assert.StartsWith("Changed: t1 state Failed → Ready", await CallAsync(Reviewer, "update", """{ "id": "t1", "state": "ready", "reason": "Retry." }""", lead: true), StringComparison.Ordinal);
    }

    // TASK-05: the submit tool's own time limit does not cut its checks short, nor make them run again; each check bounds its own time.
    [Fact]
    public async Task A_submits_checks_are_not_cut_short_by_the_tools_time_limit()
    {
        var slow = new SlowCheck();
        setup.Checks["slow"] = slow;
        var pipeline = setup.Create(options with
        {
            Tools = new Dictionary<string, ToolOptions>(options.Tools) { ["submit"] = options.Tools["submit"] with { Timeout = TimeSpan.FromSeconds(1) } },
            Checks = new Dictionary<string, CheckOptions>(options.Checks) { ["slow"] = new() { Use = "extension:slow" } },
        });
        Assert.True((await pipeline.Board(Owner.Tenant, Context.RunId).AddAsync("t1", new() { Title = "Run the tests", Checks = ["slow"] }, "planned", Ct)).Accepted);
        await RunAsync(pipeline, "claim", """{ "id": "t1" }""", Context);

        var submitting = RunAsync(pipeline, "submit", """{ "id": "t1" }""", Context);
        await slow.Started.Task.WaitAsync(Ct);
        setup.Time.Advance(TimeSpan.FromMinutes(5)); // far past the tool's limit
        slow.Release.SetResult(new(true, []));

        Assert.Equal("Changed: t1 state InProgress → InReview, verified false → true.", (await submitting).Content);
        Assert.Equal(1, slow.Runs);
    }

    // SEC-04: a failed check's findings come from running the agent's code, so reading them marks the agent as having read
    // untrusted content, and its next gated write asks first.
    [Fact]
    public async Task A_failed_checks_findings_are_untrusted_content()
    {
        var gated = setup.Create(options with
        {
            Gates = new Dictionary<string, GateOptions>(options.Gates) { ["untrusted"] = new() { Use = GateOptions.UntrustedContentApproval } },
            Tools = new Dictionary<string, ToolOptions>(options.Tools) { ["edit"] = options.Tools["edit"] with { Gates = ["untrusted"] } },
        });
        setup.Human.Answer(HumanAnswer.Approve);
        await AddAsync("t1", new() { Title = "Fix the parser", Checks = ["tests"] });
        var context = Context with { TaskId = "t1" };
        await RunAsync(gated, "claim", """{ "id": "t1" }""", context);
        await RunAsync(gated, "edit", "{}", context);
        testsPass = false;

        Assert.StartsWith("check tests failed: 2 tests fail.", (await RunAsync(gated, "submit", """{ "id": "t1" }""", context)).Content, StringComparison.Ordinal);
        await RunAsync(gated, "edit", "{}", context);

        Assert.Equal("gate untrusted", Assert.Single(setup.Human.Requests).Summary); // only the write after the findings asks
    }

    // TASK-06: in a task's working copy only the agent working on the task changes files; its reviewer reads them.
    [Fact]
    public async Task Only_the_agent_working_on_a_task_changes_its_working_copy()
    {
        var workspace = new InMemoryWorkspace();
        foreach (var (id, tool) in new WorkspaceTools(call => workspace.OpenWorkingCopyAsync(call.WorkingCopy, call.Agent, Ct)).Tools)
        {
            setup.Tools[id] = tool;
        }

        var files = setup.Create(options with
        {
            Tools = new Dictionary<string, ToolOptions>(options.Tools)
            {
                ["write"] = new() { Source = $"extension:{WorkspaceTools.Write}", GateExemption = "Tests only." }, ["read"] = new() { Source = $"extension:{WorkspaceTools.Read}" },
            },
            ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["all"] = [.. options.ToolSets["all"], "write", "read"] },
        });
        await AddAsync("t1", new() { Title = "Fix the parser", RequiresReview = true });
        await RunAsync(files, "claim", """{ "id": "t1" }""", Context);

        var author = await RunAsync(files, "write", """{ "path": "parser.cs", "content": "fixed" }""", Context with { TaskId = "t1" });
        var reviewer = await RunAsync(files, "write", """{ "path": "parser.cs", "content": "mine" }""", Context with { Agent = Reviewer, TaskId = "t1" });
        var read = await RunAsync(files, "read", """{ "path": "parser.cs" }""", Context with { Agent = Reviewer, TaskId = "t1" });

        Assert.Equal(("Written.", "not authorised: only the agent working on task t1 changes its working copy", "fixed"), (author.Content, reviewer.Content, read.Content));
    }

    // TASK-09, WS-03: a failed check, a review that asks for changes, and a change returned by integration are failed attempts.
    [Theory]
    [InlineData("check")]
    [InlineData("review")]
    [InlineData("integration")]
    public async Task A_task_that_runs_out_of_attempts_goes_back_to_the_lead(string failure)
    {
        await AddAsync("t1", new() { Title = "Fix the parser", Checks = ["tests"], RequiresReview = failure == "review" });
        await CallAsync(Agent, "claim", """{ "id": "t1" }""");
        var states = new List<(TaskState, string?)>();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            testsPass = failure != "check";
            await CallAsync(Agent, "submit", """{ "id": "t1" }""");
            if (failure == "review")
            {
                await CallAsync(Reviewer, "review", """{ "id": "t1", "approved": false, "reasons": "No test." }""");
            }
            else if (failure == "integration")
            {
                Assert.True((await owner.ReturnAsync("t1", "conflict in Parser.cs", Ct)).Accepted);
            }

            var task = await TaskAsync("t1");
            states.Add((task.State, task.Assignee));
        }

        Assert.Equal([(TaskState.InProgress, Agent), (TaskState.Failed, null)], states);
        Assert.Equal(
            "invalid arguments: a failed task goes back to the lead; only the lead or the owner retries it.", await CallAsync(Agent, "update", """{ "id": "t1", "state": "ready", "reason": "retry" }"""));
        Assert.Equal(
            (true, "Changed: t1 state Failed → Ready, failedAttempts 2 → 0."), await owner.EditAsync("t1", new() { State = TaskState.Ready }, "retry", Ct));
    }

    // TASK-08: a check removed from the configuration does not lock the board.
    [Fact]
    public async Task A_check_removed_from_the_configuration_does_not_lock_the_board()
    {
        await AddAsync("t1", new() { Title = "Fix the parser", Checks = ["tests"] });
        var without = setup.Create(options with { Checks = new Dictionary<string, CheckOptions>() }).Board(Owner.Tenant, Context.RunId);

        Assert.True((await without.AddAsync("t2", new() { Title = "Another" }, "planned", Ct)).Accepted);
        Assert.False((await without.AddAsync("t3", new() { Title = "Bad", Checks = ["tests"] }, "planned", Ct)).Accepted);
    }

    [Fact]
    public async Task Submitting_a_task_whose_check_was_removed_from_the_configuration_is_refused()
    {
        await AddAsync("t1", new() { Title = "Fix the parser", Checks = ["tests"] });
        await CallAsync(Agent, "claim", """{ "id": "t1" }""");
        var without = setup.Create(options with { Checks = new Dictionary<string, CheckOptions>() });

        var refused = (await RunAsync(without, "submit", """{ "id": "t1" }""", Context)).Content;

        Assert.Contains("check tests no longer exists in the configuration.", refused, StringComparison.Ordinal);
        Assert.Equal(TaskState.InProgress, (await TaskAsync("t1")).State);
    }

    // The work's task must be on the board.
    [Fact]
    public async Task Work_for_a_task_that_is_not_on_the_board_is_refused()
    {
        var kit = Kit(new() { Enabled = true });

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Runner.RunAsync(new Work(Agent, "Fix it.") { TaskId = "t9" }, Ct));

        Assert.StartsWith("The work is for task t9, which is not on the board.", refused.Message);
    }

    // TASK-09, COST-02: the task's budget is a budget level of the turns on it.
    [Fact]
    public async Task A_task_that_runs_out_of_budget_goes_back_to_the_lead()
    {
        var kit = Kit(new() { Enabled = true, Budget = 0.001m });
        var work = new Work(Agent, "Fix the parser.") { TaskId = "t1" };
        await kit.Runner.Board(null, work.RunId).AddAsync("t1", new() { Title = "Fix the parser" }, "planned", Ct);
        kit.Model.Reply(
            new ContentReceived(new ToolUseContent("call-0", "claim", Args("""{ "id": "t1" }"""))), new UsageReported(new Usage(1000, 0, 0, 0)),
            new Stopped(StopReason.WantsTools));

        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal((HandoffReason.BudgetExhausted, "the task's cost budget is used up"), (result.Handoff!.Reason, result.Handoff.Detail));
        var task = Assert.Single(await kit.Runner.Board(null, work.RunId).ReadAsync(Ct));
        Assert.Equal((TaskState.Failed, null, 0.001m), (task.State, task.Assignee, task.Spent));
    }

    // CTX-01, CTX-09, REC-06: the agent sees its task, the work's placeholders, and only its task's record entries.
    [Fact]
    public async Task The_volatile_context_holds_the_agents_task_and_only_its_record_entries()
    {
        var kit = Kit(new() { Enabled = true }, agent => agent with
        {
            Context = new() { RecordScope = RecordScope.Task, OperatingFacts = ["Working on {{work.task.id}}, {{work.task.title}}, in run {{work.id}}."] },
        });
        var work = new Work(Agent, "Fix the parser.") { TaskId = "t1" };
        await kit.Runner.Board(null, work.RunId).AddAsync("t1", new() { Title = "Fix the parser", AcceptanceCriteria = ["Empty files parse."] }, "planned", Ct);
        await kit.Storage.Records.TryAppendAsync(null, new RecordEntry(work.RunId, 1, "lead", kit.Time.GetUtcNow(), new Finding("The lexer is slow."), "t2"), Ct);
        await kit.Storage.Records.TryAppendAsync(null, new RecordEntry(work.RunId, 2, "lead", kit.Time.GetUtcNow(), new Finding("Empty input crashes."), "t1"), Ct);
        kit.Model.Reply("Done.");

        await kit.Runner.RunAsync(work, Ct);

        Assert.Equal(
            $"""
            <context>
            <data source="record">
            r2 finding: Empty input crashes.
            </data>
            <data source="task">
            t1 Fix the parser: Ready
            Acceptance criteria:
            - Empty files parse.
            </data>
            Working on t1, Fix the parser, in run {work.RunId}.
            </context>
            """.ReplaceLineEndings("\n"),
            Assert.Single(kit.Model.Requests).History[^1].Content.OfType<TextContent>().Single().Text);
    }

    // TOOL-06: a gate reads the board, and the task the agent works on.
    [Fact]
    public async Task A_gate_can_require_the_agents_task_to_be_in_progress()
    {
        await AddAsync("t1", new() { Title = "Fix the parser" });
        var context = Context with { TaskId = "t1" };

        var early = await RunAsync(pipeline, "edit", "{}", context);
        await CallAsync(Agent, "claim", """{ "id": "t1" }""");

        Assert.Equal("policy violation: the task must be in progress", early.Content);
        Assert.Null((await RunAsync(pipeline, "edit", "{}", context)).Error);
    }

    // CAP-02, CAP-03.
    [Fact]
    public void The_task_tools_need_the_task_board()
    {
        var options = Options(("claim", new() { Source = "builtin:tasks.claim" }));

        Assert.Equal(
            [(ValidationPhase.Capabilities, "tools.claim.source", "needs the taskBoard capability, which is off.")],
            options.Validate().Select(error => (error.Phase, error.Path, error.Problem)));
    }

    private static OfficinaOptions Configure(OfficinaOptions options) => options with
    {
        Checks = new Dictionary<string, CheckOptions> { ["tests"] = new() { Use = "extension:tests" } },
        Capabilities = new() { TaskBoard = new() { Enabled = true, MaxAttempts = 2 } },
    };

    /// <summary>A test kit whose agent <c>dev</c> may claim tasks, with the default model at $1 per million input tokens.</summary>
    private static TestKit Kit(TaskBoardOptions board, Func<AgentDefinition, AgentDefinition>? configure = null)
    {
        var options = Options(("claim", new() { Source = "builtin:tasks.claim" }));
        options = options with
        {
            Agents = new Dictionary<string, AgentDefinition> { [Agent] = (configure ?? (agent => agent))(options.Agents[Agent]) },
            Capabilities = new() { TaskBoard = board },
            Providers = new Dictionary<string, ProviderOptions>
            {
                [ProviderOptions.ClaudeName] = ProviderOptions.Claude with { Prices = new Dictionary<string, ModelPrice> { ["claude-opus-5-5"] = new() { Input = 1 } } },
            },
        };
        return new TestKit(options, capabilities: new ProviderCapabilities { TurnScopedMessages = true });
    }

    private async Task AddAsync(string id, TaskEdit task) => Assert.True((await owner.AddAsync(id, task, "planned", Ct)).Accepted);

    private async Task<BoardTask> TaskAsync(string id) => (await owner.ReadAsync(Ct)).Single(task => task.Id == id);

    /// <summary>Calls a task tool as an agent, and returns what the agent reads.</summary>
    private async Task<string> CallAsync(string agent, string tool, string arguments, bool lead = false) =>
        (await RunAsync(pipeline, tool, arguments, Context with { Agent = agent, Lead = lead })).Content;

    /// <summary>Stands in for a test command; two tests fail when it fails.</summary>
    private sealed class TestCommand(Func<bool> passes) : ICheck
    {
        public ValueTask<CheckResult> RunAsync(CheckContext context, CancellationToken ct) =>
            ValueTask.FromResult(passes() ? new CheckResult(true, []) : new CheckResult(false, ["2 tests fail"]));
    }

    /// <summary>Allows a call only while the agent's task is in progress.</summary>
    private sealed class TaskInProgress : IGate
    {
        public async ValueTask<GateDecision> EvaluateAsync(GateContext context, CancellationToken ct) =>
            (await context.Board!.ReadAsync(ct)).Any(task => task.Id == context.Board.TaskId && task.State == TaskState.InProgress)
                ? GateDecision.Allow
                : GateDecision.Deny("the task must be in progress");
    }

    /// <summary>A check that runs until the test releases it.</summary>
    private sealed class SlowCheck : ICheck
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CheckResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Runs { get; private set; }

        public async ValueTask<CheckResult> RunAsync(CheckContext context, CancellationToken ct)
        {
            Runs++;
            Started.TrySetResult();
            return await Release.Task.WaitAsync(ct);
        }
    }
}
