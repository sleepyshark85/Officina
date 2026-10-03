using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Reports;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tasks;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>
/// The team (TEAM) on the test kit: the agent <c>team</c> is a team led by <c>lead</c>, with two <c>developer</c>s and one
/// <c>reviewer</c>. The lead plans and decides with <c>create</c> and <c>update</c>; developers <c>submit</c> their tasks and
/// may <c>message</c> others and <c>hold</c> (a tool that waits for what each test says); the reviewer <c>review</c>s. Each
/// agent's replies are scripted by its work, since agents call the model in whatever order they run.
/// </summary>
public class TeamTests
{
    private static readonly Caller Ann = new("ann", null, new HashSet<string>(), new Dictionary<string, string>());

    private static readonly string[] DeveloperWork = ["Do task a,", "Do task b,"];

    /// <summary>What the <c>hold</c> tool does, given the task of the agent that calls it.</summary>
    private Func<string?, CancellationToken, Task> hold = (_, _) => Task.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // TEAM-01, TEAM-02, TEAM-03, TEAM-08, TASK-06: the lead plans, two developers work at once, another agent reviews, and the lead reports.
    [Fact]
    public async Task The_lead_plans_developers_work_at_once_another_agent_reviews_and_the_lead_reports()
    {
        var kit = Kit();
        var both = new Barrier(2);
        hold = (_, ct) => Task.Run(() => both.SignalAndWait(ct), ct); // each developer waits here until the other arrives
        Lead(kit, "You lead a team", Create("a", review: true), Create("b", review: true));
        Lead(kit, "Every task is done").Reply("a and b are done.");
        foreach (var task in new[] { "a", "b" })
        {
            Work(kit, task).CallTools(("hold", "{}")).CallTools(("submit", $$"""{ "id": "{{task}}" }""")).Reply("Submitted.");
            kit.Model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith($"Review task {task},", StringComparison.Ordinal))
                .CallTools(("review", $$"""{ "id": "{{task}}", "approved": true, "reasons": "It meets its criteria." }""")).Reply("Approved.");
        }

        var work = new Work("team", "Build a calculator.") { Caller = Ann };
        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal((AgentOutcome.Completed, "a and b are done."), (result.Outcome, result.Output));
        var history = await kit.Runner.Board(null, work.RunId).HistoryAsync(Ct);
        Assert.Equal(
            ["developer[1]", "developer[2]"],
            history.Where(change => change.Reason == "claimed").Select(change => change.By).Order(StringComparer.Ordinal));
        Assert.Equal(["reviewer[1]", "reviewer[1]"], history.Where(change => change.Reason.StartsWith("approved", StringComparison.Ordinal)).Select(change => change.By));
        Assert.All(await kit.Runner.Board(null, work.RunId).ReadAsync(Ct), task => Assert.Equal(TaskState.Done, task.State));

        var events = await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct);
        Assert.Equal(
            [(AgentStatus.Working, "task:a"), (AgentStatus.Waiting, "for the review of a"), (AgentStatus.Idle, null), (AgentStatus.Finished, null)],
            Statuses(events, events.First(coreEvent => coreEvent.Step == "task:a").Agent));
        Assert.Equal(
            ["lead", "developer[1]", "developer[2]", "reviewer[1]"],
            events.Where(coreEvent => coreEvent.Payload is AgentStatusChanged { Status: AgentStatus.Finished }).Select(coreEvent => coreEvent.Agent));

        // TEAM-04: an agent sees only its own work, never another agent's.
        var requests = kit.Model.Requests;
        Assert.All(requests.Where(request => ScriptedModelProvider.WorkOf(request).StartsWith("You are developer", StringComparison.Ordinal)), request =>
            Assert.Single(DeveloperWork, task => request.History.Any(message => message.Content.OfType<TextContent>().Any(text => text.Text.Contains(task, StringComparison.Ordinal)))));

        // Each agent's work names the tools by the names it is offered, not the built-ins' ids, and a task with no checks says none passed.
        var works = requests.Select(ScriptedModelProvider.WorkOf).ToList();
        Assert.Contains(works, work => work.StartsWith("You lead a team", StringComparison.Ordinal) && work.Contains("on the board with create:", StringComparison.Ordinal));
        Assert.Contains(works, work => work.Contains("Do task a, then submit it with submit;", StringComparison.Ordinal));
        var review = works.First(work => work.StartsWith("Review task a,", StringComparison.Ordinal));
        Assert.Contains("ask for changes with review, giving your reasons.", review, StringComparison.Ordinal);
        Assert.DoesNotContain("checks passed", review, StringComparison.Ordinal);
        Assert.DoesNotContain("tasks.", string.Concat(works), StringComparison.Ordinal);

        // RUN-10: cost by agent and by definition.
        Assert.Contains("developer", CostBreakdown.Of(events).ByDefinition.Keys);
    }

    // TEAM-09, TASK-09: work that ends unfinished goes back to the lead with the reason, and the lead retries it.
    [Fact]
    public async Task Work_that_ends_unfinished_goes_back_to_the_lead_who_retries_it()
    {
        var kit = Kit();
        Lead(kit, "You lead a team", Create("a"));
        Lead(kit, "Tasks failed and came back to you").CallTools(("update", """{ "id": "a", "state": "ready", "reason": "Try again." }""")).Reply("Retried.");
        Lead(kit, "Every task is done").Reply("Done.");
        Work(kit, "a").Reply("I give up.").CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.");

        var work = new Work("team", "Build it.") { Caller = Ann };
        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal((AgentOutcome.Completed, "Done."), (result.Outcome, result.Output));
        var told = kit.Model.Requests.First(request => ScriptedModelProvider.WorkOf(request).StartsWith("Tasks failed", StringComparison.Ordinal));
        Assert.Contains("a failed: developer[1] ended its turn without submitting the task", ScriptedModelProvider.WorkOf(told), StringComparison.Ordinal);
        Assert.Equal(
            [("developer[1]", "claimed"), ("team", "developer[1] ended its turn without submitting the task"), ("lead", "Try again."), ("developer[1]", "claimed")],
            (await kit.Runner.Board(null, work.RunId).HistoryAsync(Ct)).Where(change => change.Tasks.Any(task => task.Id == "a")).Skip(1).Take(4)
                .Select(change => (change.By, change.Reason)));
    }

    // TEAM-09, RUN-05: each agent of a role has a budget of its own; one that runs out is stopped, and its task goes back to the lead.
    [Fact]
    public async Task Each_agent_has_its_own_budget_and_one_that_runs_out_is_stopped()
    {
        var kit = Kit(developer => developer with { Budget = new() { Total = new() { Tokens = 100 } } });
        var firstStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hold = (_, ct) => firstStopped.Task.WaitAsync(ct);
        var work = new Work("team", "Build it.") { Caller = Ann };
        _ = WatchAsync(kit, work.RunId, coreEvent => coreEvent is { Agent: "developer[1]", Payload: AgentStatusChanged { Status: AgentStatus.Failed } }, firstStopped);
        Lead(kit, "You lead a team", Create("a"), Create("b"), ("update", """{ "id": "b", "assignee": "developer[2]", "reason": "Split the work." }"""));
        Lead(kit, "Tasks failed and came back to you").Reply("Leave it.");
        Lead(kit, "No task can start").Reply("Leave it.");
        Work(kit, "a").Reply(
            new ContentReceived(new ToolUseContent("spend", "message", Args("""{ "to": "lead", "text": "Starting." }"""))), new UsageReported(new Usage(1000, 0, 0, 0)),
            new Stopped(StopReason.WantsTools));
        Work(kit, "b").CallTools(("hold", "{}")).CallTools(("submit", """{ "id": "b" }""")).Reply("Submitted.");

        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal(HandoffReason.NoProgress, result.Handoff?.Reason);
        var tasks = await kit.Runner.Board(null, work.RunId).ReadAsync(Ct);
        Assert.Equal([("a", TaskState.Failed), ("b", TaskState.Done)], tasks.Select(task => (task.Id, task.State)));
        Assert.Contains(
            "a failed: developer[1]: the agent's token budget is used up",
            ScriptedModelProvider.WorkOf(kit.Model.Requests.First(request => ScriptedModelProvider.WorkOf(request).StartsWith("Tasks failed", StringComparison.Ordinal))),
            StringComparison.Ordinal);
    }

    // RUN-05, HITL-04: agents that run out of the run's budget at once each ask the owner, and the owner's yes goes on once for all of them.
    [Fact]
    public async Task Agents_that_run_out_of_the_run_budget_at_once_go_on_once_for_the_owners_yes()
    {
        var human = new PairedHuman();
        var kit = Kit(options => options with
        {
            Run = options.Run with { Budget = new() { Cost = 1 } },
            Capabilities = options.Capabilities with { HumanInteraction = new() { Enabled = true, SignOffs = [SignOff.RunBudgetExceeded] } },
            Providers = new Dictionary<string, ProviderOptions>
            {
                [ProviderOptions.ClaudeName] = ProviderOptions.Claude with { Prices = new Dictionary<string, ModelPrice> { ["claude-opus-5-5"] = new() { Input = 1 } } },
            },
        }, human);
        var both = new Barrier(2);
        hold = (_, ct) => Task.Run(() => both.SignalAndWait(ct), ct); // both have spent before either checks the budget again
        Lead(kit, "You lead a team", Create("a"), Create("b"));
        Lead(kit, "Every task is done").Reply("Done.");
        foreach (var task in new[] { "a", "b" })
        {
            Work(kit, task).Reply(Spend(task, 1, 600_000)).Reply(Spend(task, 2, 500_000)).CallTools(("submit", $$"""{ "id": "{{task}}" }""")).Reply("Submitted.");
        }

        var result = await kit.RunAsync("team", "Build it.", Ct);

        // $1.20 spent: both ask, and the budget goes to $2. $2.20 spent: both ask again; had each yes counted, the budget would have been $3 already.
        Assert.Equal((AgentOutcome.Completed, 4), (result.Outcome, human.Asked));

        static ModelEvent[] Spend(string task, int call, long tokens) =>
            [new ContentReceived(new ToolUseContent($"{task}-{call}", "hold", Args("{}"))), new UsageReported(new Usage(tokens, 0, 0, 0)), new Stopped(StopReason.WantsTools)];
    }

    // RUN-06, TEAM-09: the owner stops one agent of a run's team; its task goes back to the lead, and the team goes on.
    [Fact]
    public async Task The_owner_stops_one_agent_of_the_team_and_its_task_goes_back_to_the_lead()
    {
        var kit = Kit();
        var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hold = async (_, ct) =>
        {
            working.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        Lead(kit, "You lead a team", Create("a"));
        Lead(kit, "Tasks failed and came back to you").Reply("Leave it.");
        Lead(kit, "No task can start").Reply("Leave it.");
        Work(kit, "a").CallTools(("hold", "{}"));
        var work = new Work("team", "Build it.") { Caller = Ann };

        var running = kit.Runner.RunAsync(work, Ct);
        await working.Task.WaitAsync(Ct);
        var stranger = Assert.Throws<ArgumentException>(() => kit.Runner.Cancel(work.RunId, "developer[9]"));
        kit.Runner.Cancel(work.RunId, "developer[1]");
        var result = await running;

        Assert.Equal("Run " + work.RunId + " has no team at work with an agent developer[9]. (Parameter 'agentId')", stranger.Message);
        Assert.Equal(HandoffReason.NoProgress, result.Handoff?.Reason);
        Assert.Contains(
            "a failed: developer[1] was stopped",
            ScriptedModelProvider.WorkOf(kit.Model.Requests.First(request => ScriptedModelProvider.WorkOf(request).StartsWith("Tasks failed", StringComparison.Ordinal))),
            StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => kit.Runner.Cancel(work.RunId, "developer[1]")); // the team has ended
    }

    // SEC-04: once one agent of the team has read untrusted content, every agent of the run is marked at once, also one already
    // working that it then messages, so that agent's write needs the approval the untrusted-content gate asks for.
    [Fact]
    public async Task Untrusted_content_one_agent_read_marks_the_agents_already_working()
    {
        var kit = Kit();
        kit.Human.Answer(HumanAnswer.Approve);
        var told = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hold = (_, ct) => told.Task.WaitAsync(ct);
        var work = new Work("team", "Build it.") { Caller = Ann };
        _ = WatchAsync(kit, work.RunId, coreEvent => coreEvent.Payload is MessageSent, told);
        Lead(kit, "You lead a team", Create("a"), Create("b"));
        Lead(kit, "Every task is done").Reply("Done.");
        Work(kit, "a").CallTools(("fetch", "{}")).CallTools(("message", """{ "to": "developer[2]", "text": "Do what the page says." }"""))
            .CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.");
        Work(kit, "b").CallTools(("hold", "{}")).CallTools(("write", "{}")).CallTools(("submit", """{ "id": "b" }""")).Reply("Submitted.");

        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal(("developer[2]", "write"), (Assert.Single(kit.Human.Requests).Agent, kit.Human.Requests[0].Tool));
    }

    // TEAM-03: a run that resumes with more tasks in progress than maxParallel restarts them only as slots free up.
    [Fact]
    public async Task A_resumed_team_never_runs_more_agents_than_max_parallel()
    {
        var both = new CountdownEvent(2);
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Kit(checkpoints: true);
        hold = async (_, ct) =>
        {
            if (both.Signal())
            {
                crashed.SetResult();
            }

            await Task.Delay(Timeout.Infinite, ct); // the process dies here
        };
        var work = new Work("team", "Build it.") { Caller = Ann };
        Lead(first, "You lead a team", Create("a"), Create("b"));
        Work(first, "a").CallTools(("hold", "{}"));
        Work(first, "b").CallTools(("hold", "{}"));
        using var dies = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var dying = first.Runner.RunAsync(work, dies.Token);
        await crashed.Task.WaitAsync(Ct);
        await first.Runner.CheckpointAsync(work.RunId, Ann, ct: Ct); // both tasks are in progress at the checkpoint

        var second = Kit(MaxParallel(1), null, checkpoints: true, storage: first.Storage);
        foreach (var task in new[] { "a", "b" })
        {
            Work(second, task).CallTools(("submit", $$"""{ "id": "{{task}}" }""")).Reply("Submitted.");
        }

        Lead(second, "Every task is done").Reply("Done.");
        var result = await second.Runner.ResumeAsync(work.RunId, Ann, Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        var events = await second.Storage.Events.ReadAsync(null, work.RunId, 0, Ct);
        Assert.Equal(1, Peak(events.SkipWhile(coreEvent => coreEvent.Payload is not RunResumed)));
        await dies.CancelAsync();
        await dying;
    }

    // TEAM-03: when reviewers ask for changes at once, the authors go back to work only as slots free up.
    [Fact]
    public async Task Authors_whose_work_comes_back_never_run_more_agents_than_max_parallel()
    {
        var kit = Kit(options => options with
        {
            Agents = new Dictionary<string, AgentDefinition>(options.Agents)
            {
                ["team"] = options.Agents["team"] with
                {
                    Pattern = options.Agents["team"].Pattern with
                    {
                        MaxParallel = 2, Roles = new Dictionary<string, RoleOptions> { ["developer"] = new() { Max = 2 }, ["reviewer"] = new() { Max = 2 } },
                    },
                },
            },
        }, null);
        Lead(kit, "You lead a team", Create("a", review: true), Create("b", review: true));
        Lead(kit, "Every task is done").Reply("Done.");
        foreach (var task in new[] { "a", "b" })
        {
            Work(kit, task).CallTools(("submit", $$"""{ "id": "{{task}}" }""")).Reply("Submitted.").CallTools(("submit", $$"""{ "id": "{{task}}" }""")).Reply("Submitted again.");
            kit.Model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith($"Review task {task},", StringComparison.Ordinal))
                .CallTools(("review", $$"""{ "id": "{{task}}", "approved": false, "reasons": "Add a test." }""")).Reply("Changes asked.")
                .CallTools(("review", $$"""{ "id": "{{task}}", "approved": true, "reasons": "Tested now." }""")).Reply("Approved.");
        }

        var work = new Work("team", "Build it.") { Caller = Ann };
        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal(2, Peak(await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct)));
    }

    // RUN-06: stopping the lead ends the team, as any lead turn that does not complete does.
    [Fact]
    public async Task Stopping_the_lead_ends_the_team()
    {
        var kit = Kit();
        var planning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Work("team", "Build it.") { Caller = Ann };
        _ = WatchAsync(kit, work.RunId, coreEvent => coreEvent is { Agent: "lead", Payload: AgentStatusChanged { Status: AgentStatus.Working } }, planning);
        kit.Runner.PauseRun(work.RunId);

        var running = kit.Runner.RunAsync(work, Ct);
        await planning.Task.WaitAsync(Ct);
        kit.Runner.Cancel(work.RunId, "lead");
        kit.Runner.ResumeRun(work.RunId);
        var result = await running;

        Assert.Equal(AgentOutcome.HandedOff, result.Outcome);
        Assert.Empty(kit.Model.Requests); // a cancelled turn starts no model call, even once the run is resumed
        Assert.Contains((AgentStatus.Failed, "lead was stopped"), Statuses(await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct), "lead"));
        Assert.Empty(await kit.Runner.Board(null, work.RunId).ReadAsync(Ct));
    }

    // WS-01, WS-03, WS-09, TASK-05, RUN-03: each task is done in a working copy of its own, and done means integrated; a change
    // that no longer applies goes back to its author as a failed attempt, never resolved silently, and integrates once reworked.
    // A checkpoint follows each integration, and a task's copy goes when the task is done.
    [Fact]
    public async Task Each_task_is_integrated_from_its_own_working_copy_and_a_conflict_goes_back_to_its_author()
    {
        var workspace = new InMemoryWorkspace();
        workspace.Files["shared.txt"] = "start";
        var kit = Kit(options => options with
        {
            Tools = new Dictionary<string, ToolOptions>(options.Tools) {
                ["edit"] = new() { Source = $"extension:{WorkspaceTools.Write}", GateExemption = "Tests only." }, ["look"] = new() { Source = $"extension:{WorkspaceTools.Read}" },
            },
            ToolSets = new Dictionary<string, IReadOnlyList<string>>(options.ToolSets) { ["developer"] = ["submit", "edit", "look", "hold"] },
            Capabilities = options.Capabilities with
            {
                Workspace = new() { Enabled = true }, ConversationStore = new() { Enabled = true },
                Checkpoints = new() { Enabled = true, At = [CheckpointPoint.Integration] },
            },
        }, null, workspace: workspace);
        var both = new Barrier(2);
        hold = (_, ct) => Task.Run(() => both.SignalAndWait(ct), ct); // both have changed shared.txt before either submits
        Lead(kit, "You lead a team", Create("a"), Create("b"));
        Lead(kit, "Every task is done").Reply("Done.");
        foreach (var task in new[] { "a", "b" })
        {
            Work(kit, task).CallTools(("look", """{ "path": "shared.txt" }"""), ("edit", $$"""{ "path": "{{task}}.txt", "content": "{{task}}" }"""))
                .CallTools(("edit", $$"""{ "path": "shared.txt", "content": "from {{task}}" }""")).CallTools(("hold", "{}")).CallTools(("submit", $$"""{ "id": "{{task}}" }""")).Reply("Submitted.")
                .CallTools(("submit", $$"""{ "id": "{{task}}" }""")).Reply("Resolved."); // after the conflict, with its own version kept
        }

        var work = new Work("team", "Build it.") { Caller = Ann };
        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal(("a", "b"), (workspace.Files["a.txt"], workspace.Files["b.txt"]));
        var returned = Assert.Single(await kit.Runner.Board(null, work.RunId).HistoryAsync(Ct), change => change.By == "team" && change.Reason.StartsWith("it conflicts", StringComparison.Ordinal));
        var second = returned.Tasks.Single().Id;
        Assert.Equal($"from {second}", workspace.Files["shared.txt"]); // the reworked change came last
        Assert.Equal(2, (await kit.Storage.Checkpoints.ReadAsync(null, work.RunId, Ct)).Count(checkpoint => checkpoint.Point == CheckpointPoint.Integration));
        Assert.Empty(await workspace.SnapshotAsync(Ct)); // every task's copy went with its task
    }

    // TASK-05: what changes in a task's copy after it was submitted, here an edit after the submit, is not integrated: the task goes
    // back to its author, whose next submit checks the copy as it is now.
    [Fact]
    public async Task A_copy_changed_after_its_task_was_submitted_goes_back_to_its_author()
    {
        var workspace = new InMemoryWorkspace();
        var kit = Kit(options => options with
        {
            Tools = new Dictionary<string, ToolOptions>(options.Tools) { ["edit"] = new() { Source = $"extension:{WorkspaceTools.Write}", GateExemption = "Tests only." } },
            ToolSets = new Dictionary<string, IReadOnlyList<string>>(options.ToolSets) { ["developer"] = ["submit", "edit"] },
            Capabilities = options.Capabilities with { Workspace = new() { Enabled = true } },
        }, null, workspace: workspace);
        Lead(kit, "You lead a team", Create("a"));
        Lead(kit, "Every task is done").Reply("Done.");
        Work(kit, "a").CallTools(("edit", """{ "path": "a.txt", "content": "checked" }""")).CallTools(("submit", """{ "id": "a" }"""))
            .CallTools(("edit", """{ "path": "a.txt", "content": "planted" }""")).Reply("Submitted.")
            .CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted again.");

        var work = new Work("team", "Build it.") { Caller = Ann };
        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        var history = await kit.Runner.Board(null, work.RunId).HistoryAsync(Ct);
        Assert.Single(history, change => change.By == "team" && change.Reason.StartsWith("the working copy changed after its checks ran", StringComparison.Ordinal));
        Assert.Equal("planted", workspace.Files["a.txt"]); // only once a submit had checked it
    }

    // TEAM-07, TASK-06: a reviewer's helper keeps the reviewer's task, so it reads the author's copy and cannot change it; and a
    // helper's id is numbered in the run, so the reviewer's helpers in two turns never share one.
    [Fact]
    public async Task A_reviewers_helper_cannot_change_the_authors_copy_and_helper_ids_never_repeat()
    {
        var workspace = new InMemoryWorkspace();
        var kit = Kit(options => options with
        {
            Agents = new Dictionary<string, AgentDefinition>(options.Agents)
            {
                ["reviewer"] = options.Agents["reviewer"] with { Helpers = ["checker"] },
                ["checker"] = new() { Instructions = "Check.", Tools = ["checker"] },
            },
            Tools = new Dictionary<string, ToolOptions>(options.Tools)
            {
                ["edit"] = new() { Source = $"extension:{WorkspaceTools.Write}", GateExemption = "Tests only." }, ["look"] = new() { Source = $"extension:{WorkspaceTools.Read}" },
                ["helper"] = new() { Source = "builtin:team.start_helper" },
            },
            ToolSets = new Dictionary<string, IReadOnlyList<string>>(options.ToolSets)
            {
                ["developer"] = ["submit", "edit"], ["reviewer"] = ["review", "helper", "edit", "look"], ["checker"] = ["edit", "look"],
            },
            Capabilities = options.Capabilities with { Workspace = new() { Enabled = true } },
        }, null, workspace: workspace);
        Lead(kit, "You lead a team", Create("a", review: true), Create("b", review: true));
        Lead(kit, "Every task is done").Reply("Done.");
        foreach (var task in new[] { "a", "b" })
        {
            Work(kit, task).CallTools(("edit", $$"""{ "path": "{{task}}.txt", "content": "by the author" }""")).CallTools(("submit", $$"""{ "id": "{{task}}" }""")).Reply("Submitted.");
            kit.Model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith($"Review task {task},", StringComparison.Ordinal))
                .CallTools(("helper", $$"""{ "agent": "checker", "work": "Check {{task}}." }"""))
                .CallTools(("review", $$"""{ "id": "{{task}}", "approved": true, "reasons": "Checked." }""")).Reply("Approved.");
            kit.Model.When(request => ScriptedModelProvider.WorkOf(request) == $"Check {task}.")
                .CallTools(("look", $$"""{ "path": "{{task}}.txt" }""")).CallTools(("edit", $$"""{ "path": "{{task}}.txt", "content": "by the helper" }""")).Reply("Checked.");
        }

        var work = new Work("team", "Build it.") { Caller = Ann };
        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal(("by the author", "by the author"), (workspace.Files["a.txt"], workspace.Files["b.txt"]));
        var helpers = kit.Model.Requests.SelectMany(request => request.History).SelectMany(message => message.Content).OfType<ToolResultContent>()
            .Select(content => content.Text).Where(text => text.StartsWith("""<data source="tool:helper">""", StringComparison.Ordinal)).Distinct();
        Assert.Equal(2, helpers.Count(text => text.Contains("did not complete: every tool call of the iteration was refused", StringComparison.Ordinal))); // its write
        var events = await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct);
        Assert.Equal(
            ["checker[reviewer[1].1]", "checker[reviewer[1].2]"],
            events.Select(coreEvent => coreEvent.Agent).Where(agent => agent.StartsWith("checker", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal));
    }

    // WS-08: a task's copy that cannot be removed is reported, and the team goes on; its integration's checkpoint is taken first.
    [Fact]
    public async Task A_working_copy_that_cannot_be_removed_is_reported_and_the_team_goes_on()
    {
        var workspace = new InMemoryWorkspace { CloseFails = new IOException("the folder is in use") };
        var kit = Kit(options => options with
        {
            Capabilities = options.Capabilities with
            {
                Workspace = new() { Enabled = true }, ConversationStore = new() { Enabled = true },
                Checkpoints = new() { Enabled = true, At = [CheckpointPoint.Integration] },
            },
        }, null, workspace: workspace);
        Lead(kit, "You lead a team", Create("a"));
        Lead(kit, "Every task is done").Reply("Done.");
        Work(kit, "a").CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.");

        var work = new Work("team", "Build it.") { Caller = Ann };
        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        var events = await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct);
        Assert.Equal("the working copy of task a was not removed: the folder is in use", Assert.Single(events.Select(coreEvent => coreEvent.Payload).OfType<Warning>()).Text);
        Assert.Contains(events, coreEvent => coreEvent.Payload is CheckpointTaken { Point: CheckpointPoint.Integration });
    }

    // TEAM-10, HITL-04: with the sign-off on, no work starts until the owner approves the lead's plan; a denial sends it back to the lead.
    [Fact]
    public async Task No_work_starts_until_the_owner_approves_the_leads_plan()
    {
        var kit = Kit(options => options with
        {
            Capabilities = options.Capabilities with { HumanInteraction = new() { Enabled = true, SignOffs = [SignOff.PlanApproval] } },
        }, null);
        kit.Human.Answer(HumanAnswer.Deny).Answer(HumanAnswer.Approve);
        Lead(kit, "You lead a team", Create("a"));
        Lead(kit, "The owner did not approve your plan", Create("b"));
        Lead(kit, "Every task is done").Reply("Done.");
        Work(kit, "a").CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.");
        Work(kit, "b").CallTools(("submit", """{ "id": "b" }""")).Reply("Submitted.");

        var work = new Work("team", "Build it.") { Caller = Ann };
        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal([HumanRequestKind.SignOff, HumanRequestKind.SignOff], kit.Human.Requests.Select(request => request.Kind));
        Assert.Contains("b Task b", kit.Human.Requests[1].Summary, StringComparison.Ordinal); // the changed plan
        var events = await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct);
        var approved = Assert.Single(events, coreEvent => coreEvent.Payload is PlanApproved).Sequence;
        Assert.All(events.Where(coreEvent => coreEvent.Payload is TaskStatusChanged { Status: TaskState.InProgress }), claimed => Assert.True(claimed.Sequence > approved));
    }

    // TEAM-10: an owner who does not answer within run.approvalTimeout hands the run off, and no work starts.
    [Fact]
    public async Task A_plan_the_owner_does_not_answer_hands_the_run_off()
    {
        var kit = Kit(options => options with
        {
            Capabilities = options.Capabilities with { HumanInteraction = new() { Enabled = true, SignOffs = [SignOff.PlanApproval] } },
        }, new SilentHuman());
        Lead(kit, "You lead a team", Create("a"));

        var running = kit.Runner.RunAsync(new Work("team", "Build it.") { Caller = Ann }, Ct);
        while (!kit.Time.Pending.Contains(TimeSpan.FromMinutes(30))) // the owner's deadline is set
        {
            await Task.Delay(10, Ct);
        }

        kit.Time.Advance(TimeSpan.FromMinutes(30));
        var result = await running;

        Assert.Equal((AgentOutcome.HandedOff, HandoffReason.ApprovalDeniedOrTimedOut), (result.Outcome, result.Handoff?.Reason));
        Assert.DoesNotContain(kit.Model.Requests, request => ScriptedModelProvider.WorkOf(request).Contains("Do task a,", StringComparison.Ordinal));
    }

    // TEAM-05, TEAM-06: a message names its sender and recipient, is recorded, and reaches the recipient as data; only the team's agents receive one.
    [Fact]
    public async Task A_message_between_agents_is_recorded_and_reaches_its_recipient_as_data()
    {
        var kit = Kit();
        Lead(kit, "You lead a team", Create("a"));
        Lead(kit, "Every task is done").Reply("Done.");
        Work(kit, "a")
            .CallTools(("message", """{ "to": "lead", "text": "The parser needs a rewrite." }"""), ("message", """{ "to": "bob", "text": "Hi." }"""), ("message", """{ "to": "developer[1]", "text": "Me." }"""))
            .CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.");

        var work = new Work("team", "Build it.") { Caller = Ann };
        await kit.Runner.RunAsync(work, Ct);

        var events = await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct);
        var sent = Assert.Single(events, coreEvent => coreEvent.Payload is MessageSent);
        Assert.Equal(("developer[1]", new MessageSent("lead", "The parser needs a rewrite.")), (sent.Agent, (MessageSent)sent.Payload));
        var report = kit.Model.Requests.Single(request => ScriptedModelProvider.WorkOf(request).StartsWith("Every task is done", StringComparison.Ordinal));
        Assert.Contains(report.History, message => message.Content.OfType<TextContent>()
            .Any(text => text.Text == "<data source=\"agent:developer[1]\">\nThe parser needs a rewrite.\n</data>"));
        var refused = kit.Model.Requests.Last(request => ScriptedModelProvider.WorkOf(request).Contains("Do task a,", StringComparison.Ordinal))
            .History.SelectMany(message => message.Content.OfType<ToolResultContent>()).Select(content => content.Text).ToList();
        Assert.Contains(refused, text => text.Contains("bob is not another agent of your team. Send to one of: lead, developer[2], reviewer[1].", StringComparison.Ordinal));
        Assert.Contains(refused, text => text.Contains("developer[1] is not another agent of your team.", StringComparison.Ordinal));
    }

    // TEAM-09: when the team cannot go on, the lead is asked once; if it changes nothing, the team hands off.
    [Fact]
    public async Task A_team_that_cannot_go_on_asks_the_lead_and_then_hands_off()
    {
        var kit = Kit();
        Lead(kit, "You lead a team", ("create", """{ "id": "a", "title": "Test it", "role": "tester", "reason": "plan" }"""));
        Lead(kit, "No task can start").Reply("Nothing to change.");

        var result = await kit.RunAsync("team", "Test it.", Ct);

        Assert.Equal((HandoffReason.NoProgress, "the team cannot go on: a Test it: Ready, role tester"), (result.Handoff?.Reason, result.Output));
    }

    // RUN-04, TEST-12: a team resumes from its last checkpoint's board: the plan is not made again, and a task in progress goes back to its agent.
    // TEAM-10: nor is a plan the owner approved before the crash approved again (the second process's owner has no answer to give).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_team_resumes_from_its_board_after_a_crash(bool planApproval)
    {
        Func<OfficinaOptions, OfficinaOptions> approval = options => !planApproval ? options : options with
        {
            Capabilities = options.Capabilities with { HumanInteraction = new() { Enabled = true, SignOffs = [SignOff.PlanApproval] } },
        };
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Kit(approval, null, checkpoints: true);
        first.Human.Answer(HumanAnswer.Approve);
        hold = async (task, ct) =>
        {
            if (task == "a")
            {
                await bClaimed.Task.WaitAsync(ct); // so the checkpoint after a's turn has b in progress
                return;
            }

            await aDone.Task.WaitAsync(ct);
            crashed.SetResult();
            await Task.Delay(Timeout.Infinite, ct); // the process dies here
        };
        var work = new Work("team", "Build it.") { Caller = Ann };
        _ = WatchAsync(first, work.RunId, coreEvent => coreEvent.Payload is TaskStatusChanged { Task: "b", Status: TaskState.InProgress }, bClaimed);

        // Once a is done its developer is idle, and the team waits for b: nothing else happens in the process that dies.
        _ = WatchAsync(first, work.RunId, coreEvent => coreEvent is { Agent: "developer[1]", Payload: AgentStatusChanged { Status: AgentStatus.Idle } }, aDone);
        Lead(first, "You lead a team", Create("a"), Create("b"));
        Work(first, "a").CallTools(("hold", "{}")).CallTools(("submit", """{ "id": "a" }""")).Reply("Submitted.");
        Work(first, "b").CallTools(("hold", "{}"));
        using var dies = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var dying = first.Runner.RunAsync(work, dies.Token);
        await crashed.Task.WaitAsync(Ct);

        // A new process: the same storage, a runner of its own.
        hold = (_, _) => Task.CompletedTask;
        var second = Kit(approval, null, checkpoints: true, storage: first.Storage);
        Work(second, "b").CallTools(("submit", """{ "id": "b" }""")).Reply("Submitted.");
        Lead(second, "Every task is done").Reply("Both done.");
        var result = await second.Runner.ResumeAsync(work.RunId, Ann, Ct);

        Assert.Equal((AgentOutcome.Completed, "Both done."), (result.Outcome, result.Output));
        Assert.DoesNotContain(second.Model.Requests, request => ScriptedModelProvider.WorkOf(request).StartsWith("You lead a team", StringComparison.Ordinal));
        Assert.Contains("It came back to you", ScriptedModelProvider.WorkOf(second.Model.Requests[0]), StringComparison.Ordinal);
        Assert.All(await second.Runner.Board(null, work.RunId).ReadAsync(Ct), task => Assert.Equal(TaskState.Done, task.State));
        Assert.Equal((planApproval ? 1 : 0, 0), (first.Human.Requests.Count, second.Human.Requests.Count));

        // The process that died never comes back; it is stopped only so the test leaves nothing running.
        await dies.CancelAsync();
        await dying;
    }

    // TEAM-01, TEAM-04, CAP-02, CAP-03: what a team's configuration needs.
    [Fact]
    public void A_team_is_validated_before_it_runs()
    {
        var options = Configure();
        var team = options.Agents["team"];
        var broken = options with
        {
            Agents = new Dictionary<string, AgentDefinition>(options.Agents)
            {
                ["team"] = team with
                {
                    Budget = new() { Total = new() { Cost = 5 } },
                    Pattern = team.Pattern with { Roles = new Dictionary<string, RoleOptions>(team.Pattern.Roles) { ["lead"] = new(), ["pipeline"] = new() } },
                },
                ["reviewer"] = options.Agents["reviewer"] with { Context = new() { History = new() { Strategy = HistoryStrategy.Full } } },
                ["pipeline"] = new() { Instructions = "Steps.", Pattern = new() { Type = PatternOptions.Workflow, Steps = [new() { Id = "one", Agent = "team" }] } },
            },
            Capabilities = new() { Team = new() { Enabled = true }, Workspace = new() { Enabled = true } },
        };

        Assert.Equal(
            [
                (ValidationPhase.Shape, "agents.team.budget.total"),
                (ValidationPhase.Shape, "agents.team.pattern.roles.lead"),
                (ValidationPhase.Shape, "agents.reviewer.context.history.strategy"),
                (ValidationPhase.Shape, "agents.team.pattern.roles.pipeline"),
                (ValidationPhase.Shape, "agents.pipeline.pattern.steps[0]"),
                (ValidationPhase.Capabilities, "capabilities.team.enabled"),
                (ValidationPhase.Capabilities, "tools.create.source"),
                (ValidationPhase.Capabilities, "tools.update.source"),
                (ValidationPhase.Capabilities, "tools.submit.source"),
                (ValidationPhase.Capabilities, "tools.review.source"),
                (ValidationPhase.Capabilities, "agents.reviewer.context.history.strategy"),
            ],
            broken.Validate().Select(error => (error.Phase, error.Path)));
        Assert.Equal(
            [(ValidationPhase.Capabilities, "tools.message.source"), (ValidationPhase.Capabilities, "agents.team.pattern.type")],
            (options with { Capabilities = options.Capabilities with { Team = new() } }).Validate().Select(error => (error.Phase, error.Path)));
    }

    // TASK-08: no turn runs on a task that is done, cancelled or failed.
    [Fact]
    public async Task No_turn_runs_on_a_task_that_has_ended()
    {
        var kit = Kit();
        var work = new Work("developer", "Fix it.") { TaskId = "a" };
        var board = kit.Runner.Board(null, work.RunId);
        await board.AddAsync("a", new() { Title = "Fix it" }, "planned", Ct);
        await board.EditAsync("a", new() { State = TaskState.Cancelled }, "not needed", Ct);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Runner.RunAsync(work, Ct));

        Assert.StartsWith("The work is for task a, which is Cancelled", refused.Message, StringComparison.Ordinal);
    }

    // TASK-03, LOOP-08: board calls in one reply take effect in the order they were made, so a task may depend on one created just before it.
    [Fact]
    public async Task A_task_may_depend_on_one_the_lead_creates_in_the_same_reply()
    {
        var kit = Kit();
        Lead(kit, "You lead a team", Create("a"), ("create", """{ "id": "b", "title": "Task b", "role": "developer", "dependsOn": ["a"], "reason": "plan" }"""));
        Lead(kit, "Every task is done").Reply("Done.");
        foreach (var task in new[] { "a", "b" })
        {
            Work(kit, task).CallTools(("submit", $$"""{ "id": "{{task}}" }""")).Reply("Submitted.");
        }

        var work = new Work("team", "Build it.") { Caller = Ann };
        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal((AgentOutcome.Completed, "Done."), (result.Outcome, result.Output));
        var history = await kit.Runner.Board(null, work.RunId).HistoryAsync(Ct);
        Assert.Equal(["a added as Ready", "b added as Proposed"], history.Take(2).Select(change => change.What));
        Assert.All(await kit.Runner.Board(null, work.RunId).ReadAsync(Ct), task => Assert.Equal(TaskState.Done, task.State));
    }

    private static (string, string) Create(string id, bool review = false) =>
        ("create", $$"""{ "id": "{{id}}", "title": "Task {{id}}", "role": "developer", "requiresReview": {{(review ? "true" : "false")}}, "reason": "plan" }""");

    /// <summary>Scripts the lead's turns whose work starts with <paramref name="work"/>: its tool calls, then a reply.</summary>
    private static ModelScript Lead(TestKit kit, string work, params (string Tool, string Arguments)[] calls)
    {
        var script = kit.Model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith(work, StringComparison.Ordinal));
        return calls.Length == 0 ? script : script.CallTools(calls).Reply("Planned.");
    }

    /// <summary>Scripts the turns of the developer who does a task.</summary>
    private static ModelScript Work(TestKit kit, string task) =>
        kit.Model.When(request => ScriptedModelProvider.WorkOf(request).Contains($"Do task {task},", StringComparison.Ordinal));

    /// <summary>The most agents that worked at once: each starts working with a status event and ends with its step.</summary>
    private static int Peak(IEnumerable<CoreEvent> events)
    {
        var (now, peak) = (0, 0);
        foreach (var payload in events.Select(coreEvent => coreEvent.Payload))
        {
            now += payload switch { AgentStatusChanged { Status: AgentStatus.Working } => 1, StepEnded => -1, _ => 0 };
            peak = Math.Max(peak, now);
        }

        return peak;
    }

    private static Func<OfficinaOptions, OfficinaOptions> MaxParallel(int most) => options => options with
    {
        Agents = new Dictionary<string, AgentDefinition>(options.Agents)
        {
            ["team"] = options.Agents["team"] with { Pattern = options.Agents["team"].Pattern with { MaxParallel = most } },
        },
    };

    private static List<(AgentStatus, string?)> Statuses(IReadOnlyList<CoreEvent> events, string agent) =>
        [.. events.Where(coreEvent => coreEvent.Agent == agent).Select(coreEvent => coreEvent.Payload).OfType<AgentStatusChanged>().Select(changed => (changed.Status, changed.Detail))];

    /// <summary>Sets <paramref name="seen"/> once the run publishes an event that matches.</summary>
    private static async Task WatchAsync(TestKit kit, string runId, Func<CoreEvent, bool> matches, TaskCompletionSource seen)
    {
        await foreach (var coreEvent in kit.Runner.Events.ReadAsync(null, runId, 0, Ct))
        {
            if (matches(coreEvent))
            {
                seen.TrySetResult();
                return;
            }
        }
    }

    private TestKit Kit(Func<AgentDefinition, AgentDefinition>? developer = null, bool checkpoints = false, InMemoryStorage? storage = null) =>
        Kit(options => options, null, developer, checkpoints, storage);

    private TestKit Kit(
        Func<OfficinaOptions, OfficinaOptions> configure, IHumanChannel? human, Func<AgentDefinition, AgentDefinition>? developer = null, bool checkpoints = false,
        InMemoryStorage? storage = null, InMemoryWorkspace? workspace = null)
    {
        var options = configure(Configure(developer, checkpoints));
        var tools = new Dictionary<string, ITool>
        {
            ["hold"] = new FakeTool(ToolKind.Read, run: async (call, ct) => { await hold(call.Board?.TaskId, ct); return ToolResult.Success("held"); }),
            ["fetch"] = new FakeTool(ToolKind.Read), ["write"] = new FakeTool(ToolKind.Write),
        };
        if (workspace is not null)
        {
            foreach (var (id, tool) in new WorkspaceTools(call => workspace.OpenWorkingCopyAsync(call.WorkingCopy, call.Agent, Ct)).Tools)
            {
                tools[id] = tool;
            }
        }

        return new TestKit(options, tools, storage: storage, human: human, workspace: workspace);
    }

    private static OfficinaOptions Configure(Func<AgentDefinition, AgentDefinition>? developer = null, bool checkpoints = false)
    {
        var options = Options(
            ("create", new() { Source = "builtin:tasks.create" }),
            ("update", new() { Source = "builtin:tasks.update" }),
            ("submit", new() { Source = "builtin:tasks.submit_for_review" }),
            ("review", new() { Source = "builtin:tasks.review" }),
            ("message", new() { Source = "builtin:team.message" }),
            ("hold", Extension("hold")),
            ("fetch", Extension("fetch") with { Untrusted = true }),
            ("write", Extension("write") with { Gates = ["untrusted"] }));
        return options with
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["team"] = new()
                {
                    Instructions = "A team.",
                    Pattern = new()
                    {
                        Type = PatternOptions.Team, Lead = "lead",
                        Roles = new Dictionary<string, RoleOptions> { ["developer"] = new() { Max = 2 }, ["reviewer"] = new() },
                    },
                },
                ["lead"] = new() { Instructions = "Lead.", Tools = ["lead"] },
                ["developer"] = (developer ?? (agent => agent))(new() { Instructions = "Develop.", Tools = ["developer"] }),
                ["reviewer"] = new() { Instructions = "Review.", Tools = ["reviewer"] },
            },
            ToolSets = new Dictionary<string, IReadOnlyList<string>>
            {
                ["lead"] = ["create", "update", "message"], ["developer"] = ["submit", "message", "hold", "fetch", "write"], ["reviewer"] = ["review"],
            },
            Gates = new Dictionary<string, GateOptions> { ["untrusted"] = new() { Use = GateOptions.UntrustedContentApproval } },
            Capabilities = new()
            {
                TaskBoard = new() { Enabled = true }, Team = new() { Enabled = true },
                Checkpoints = new() { Enabled = checkpoints }, ConversationStore = new() { Enabled = checkpoints },
            },
        };
    }

    /// <summary>An owner who never answers.</summary>
    private sealed class SilentHuman : IHumanChannel
    {
        public async ValueTask<HumanAnswer> AskAsync(HumanRequest request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>The owner, who answers yes once two requests wait, so both agents have asked before either goes on.</summary>
    private sealed class PairedHuman : IHumanChannel
    {
        private readonly List<TaskCompletionSource<HumanAnswer>> waiting = [];

        public int Asked { get; private set; }

        public ValueTask<HumanAnswer> AskAsync(HumanRequest request, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<HumanAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (waiting)
            {
                Asked++;
                waiting.Add(answer);
                if (waiting.Count == 2)
                {
                    waiting.ForEach(waiter => waiter.SetResult(HumanAnswer.Approve));
                    waiting.Clear();
                }
            }

            return new(answer.Task.WaitAsync(ct));
        }
    }
}
