using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Memory;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;
using FactEntry = Sleepyshark.Officina.Core.Records.Fact;

namespace Sleepyshark.Officina.Core.Tests.Checkpoints;

/// <summary>
/// Checkpoints, rollback and resume (RUN-03, RUN-04, RUN-07, RUN-08), on the test kit's in-memory storage and workspace. The
/// agent <c>lead</c> runs a workflow of two steps, both turns of <c>dev</c>, who keeps its conversation and is offered
/// <c>fact</c> (the run record), <c>write</c> (a file in its working copy) and <c>deploy</c>, an irreversible call outside the core's state.
/// </summary>
public class CheckpointTests
{
    private static readonly Caller Ann = new("ann", null, new HashSet<string>(), new Dictionary<string, string>());

    private readonly InMemoryWorkspace workspace = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // RUN-03.
    [Theory]
    [InlineData("", new[] { CheckpointPoint.Start, CheckpointPoint.Turn, CheckpointPoint.Turn })]
    [InlineData("step", new[] { CheckpointPoint.Start, CheckpointPoint.Step, CheckpointPoint.Step })]
    [InlineData("turn,step", new[] { CheckpointPoint.Start, CheckpointPoint.Turn, CheckpointPoint.Step, CheckpointPoint.Turn, CheckpointPoint.Step })]
    [InlineData("off", new CheckpointPoint[0])]
    public async Task Checkpoints_are_taken_at_the_start_and_where_the_configuration_says(string at, CheckpointPoint[] expected)
    {
        var kit = Kit(Checkpoints(at), out _);
        TwoSteps(kit);

        var work = new Work("lead", "go");
        await kit.Runner.RunAsync(work, Ct);

        Assert.Equal(expected, (await kit.Storage.Checkpoints.ReadAsync(null, work.RunId, Ct)).Select(checkpoint => checkpoint.Point));
        Assert.Equal(expected.Length, (await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct)).Count(coreEvent => coreEvent.Payload is CheckpointTaken));
    }

    [Fact]
    public async Task A_checkpoint_on_demand_is_always_taken_and_one_after_an_integration_only_when_listed()
    {
        var kit = Kit(Checkpoints("turn"), out _);
        TwoSteps(kit);
        var work = new Work("lead", "go");
        await kit.Runner.RunAsync(work, Ct);

        var onDemand = await kit.Runner.CheckpointAsync(work.RunId, ct: Ct);
        var integration = await kit.Runner.CheckpointAsync(work.RunId, point: CheckpointPoint.Integration, ct: Ct);

        Assert.Equal((3, CheckpointPoint.OnDemand), (onDemand!.Number, onDemand.Point));
        Assert.Null(integration);
        Assert.Equal(4, (await kit.Runner.CheckpointsAsync(work.RunId, ct: Ct)).Count);
        Assert.Equal(CheckpointPoint.Integration, (await Kit(Checkpoints("integration"), out _).AfterRunAsync(TwoSteps, point: CheckpointPoint.Integration))!.Point);
    }

    // RUN-08, TEST-24.
    [Fact]
    public async Task A_rollback_restores_the_state_and_the_working_copy_together_and_lists_what_it_cannot_undo()
    {
        var kit = Kit(Checkpoints("step"), out var deployed);
        TwoSteps(kit);
        var work = new Work("lead", "go") { Caller = Ann };
        await kit.Runner.RunAsync(work, Ct);
        var copy = (InMemoryWorkspace.Copy)await workspace.OpenWorkingCopyAsync("w", "dev", Ct);
        Assert.Equal(["a.txt", "b.txt"], copy.Files.Keys.Order());
        Assert.Equal(2, (await kit.Storage.Records.ReadAsync(null, work.RunId, Ct)).Count);
        Assert.Equal(2, kit.Storage.Conversations.Turns.Count);

        var report = await kit.Runner.RollbackAsync(work.RunId, 1, Ann, Ct);

        // The first step's work stays; the second's is gone, in the record, the conversation and the working copy.
        Assert.Equal(1, report.To.Number);
        Assert.Equal(["a.txt"], ((InMemoryWorkspace.Copy)await workspace.OpenWorkingCopyAsync("w", "dev", Ct)).Files.Keys);
        Assert.Equal(["a"], (await kit.Storage.Records.ReadAsync(null, work.RunId, Ct)).Select(entry => ((FactEntry)entry.Item).Subject));
        Assert.Single(kit.Storage.Conversations.Turns);
        Assert.Equal([0, 1], (await kit.Runner.CheckpointsAsync(work.RunId, Ann, Ct)).Select(checkpoint => checkpoint.Number));

        // The deploy is outside the core's state, so it is listed, not undone. The record, the file and the conversation are not listed.
        Assert.Equal(["deploy"], report.NotUndone.Select(effect => effect.Tool));
        Assert.True(report.NotUndone[0].Irreversible);
        Assert.True(report.NotUndone[0].Finished);
        Assert.Single(deployed.Calls);
        var rolledBack = (await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct)).Select(coreEvent => coreEvent.Payload).OfType<RunRolledBack>().Single();
        Assert.Equal(("deploy", 1, 0), (rolledBack.NotUndone[0].Tool, rolledBack.Checkpoint, rolledBack.MemoryChanges));
        Assert.Equal(RunStatus.Running, (await kit.Storage.Runs.ReadAsync(null, work.RunId, Ct))!.Status);
    }

    [Fact]
    public async Task A_rollback_to_the_start_removes_a_working_copy_that_did_not_exist_then()
    {
        var kit = Kit(Checkpoints("step"), out _);
        TwoSteps(kit);
        var work = new Work("lead", "go");
        await kit.Runner.RunAsync(work, Ct);

        await kit.Runner.RollbackAsync(work.RunId, 0, ct: Ct);

        Assert.Empty(await kit.Storage.Records.ReadAsync(null, work.RunId, Ct));
        Assert.Empty(kit.Storage.Conversations.Turns);
        Assert.Empty(await workspace.SnapshotAsync(Ct));
        Assert.Empty((await workspace.OpenWorkingCopyAsync("w", "dev", Ct) as InMemoryWorkspace.Copy)!.Files);
    }

    // Memory outlives runs and is shared, so a rollback leaves it alone and reports the changes made since.
    [Fact]
    public async Task A_rollback_leaves_project_memory_as_it_is_and_reports_the_changes_since_the_checkpoint()
    {
        var kit = Kit(Checkpoints("step"), out _, memory: true);
        TwoSteps(kit);
        await kit.Runner.Memory(Ann).ProposeAsync(new MemoryProposal(MemoryKind.Note, "build", "Use MSBuild."), Ct);
        await kit.Runner.Memory(Ann).ApproveAsync(1, null, Ct);
        var work = new Work("lead", "go") { Caller = Ann };
        await kit.Runner.RunAsync(work, Ct);
        await kit.Runner.Memory(Ann).ProposeAsync(new MemoryProposal(MemoryKind.Note, "tests", "Use xUnit."), Ct);
        await kit.Runner.Memory(Ann).ApproveAsync(2, null, Ct);
        var revision = (await kit.Runner.Memory(Ann).ReadAsync(Ct)).Revision;

        var report = await kit.Runner.RollbackAsync(work.RunId, 0, Ann, Ct);

        Assert.Equal(2, report.To.Memory);
        Assert.Equal([3L, 4], report.MemoryChanges.Select(change => change.Revision));
        Assert.Equal(4, revision);
        Assert.Equal(4, (await kit.Runner.Memory(Ann).ReadAsync(Ct)).Revision);
    }

    // RUN-04, RUN-07, TOOL-10, REL-03, TEST-12.
    [Fact]
    public async Task A_crash_between_an_irreversible_calls_intent_and_its_outcome_sends_the_call_to_a_human_after_the_restart()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var first = Kit(Checkpoints("step"), out var deployedBeforeCrash, deploy: async () =>
        {
            arrived.SetResult();
            await release.Task; // the process dies here; nothing after it happens in the run
            return ToolResult.Success("deployed");
        });
        TwoSteps(first);
        var work = new Work("lead", "go") { Caller = Ann };
        var dying = first.Runner.RunAsync(work, Ct);
        await arrived.Task.WaitAsync(Ct);

        // A new process: the same storage and workspace, a runner of its own.
        var second = Kit(Checkpoints("step"), out var deployedAfterRestart, storage: first.Storage);
        TwoSteps(second);
        var result = await second.Runner.ResumeAsync(work.RunId, Ann, Ct);

        // The step before the checkpoint is redone on the restored state, and the deploy is not run again: it goes to a human.
        Assert.Equal((1, 0), (deployedBeforeCrash.Calls.Count, deployedAfterRestart.Calls.Count));
        Assert.Equal((AgentOutcome.HandedOff, HandoffReason.RoutedByGate), (result.Outcome, result.Handoff!.Reason));
        var events = await second.Storage.Events.ReadAsync(null, work.RunId, 0, Ct);
        var resumed = events.Select(coreEvent => coreEvent.Payload).OfType<RunResumed>().Single();
        Assert.Equal(1, resumed.Checkpoint);
        var interrupted = Assert.Single(resumed.Interrupted);
        Assert.Equal(("deploy", true, false), (interrupted.Tool, interrupted.Irreversible, interrupted.Finished));

        // RUN-09, S08: the events of the run are one stream in one order across the restart.
        Assert.Equal(events.Count, events.Select(coreEvent => coreEvent.Sequence).Distinct().Count());
        Assert.Equal(events.OrderBy(coreEvent => coreEvent.Sequence).Select(coreEvent => coreEvent.Sequence), events.Select(coreEvent => coreEvent.Sequence));

        // The process that died never comes back; it is let go only so the test leaves nothing running.
        release.SetResult();
        await dying;
    }

    [Fact]
    public async Task A_run_resumes_only_when_checkpoints_are_on_and_it_has_not_ended_and_for_its_own_caller()
    {
        var kit = Kit(Checkpoints("step"), out _);
        TwoSteps(kit);
        var work = new Work("lead", "go") { Caller = Ann };
        await kit.Runner.RunAsync(work, Ct);

        var ended = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Runner.ResumeAsync(work.RunId, Ann, Ct));
        var stranger = await Assert.ThrowsAsync<ArgumentException>(() => kit.Runner.ResumeAsync(work.RunId, Caller.Anonymous, Ct));
        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => kit.Runner.ResumeAsync("nobody", Ann, Ct));
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => Kit(Checkpoints("off"), out _).Runner.ResumeAsync(work.RunId, Ann, Ct));

        Assert.Equal(RunStatus.Completed, (await kit.Storage.Runs.ReadAsync(null, work.RunId, Ct))!.Status);
        Assert.Contains("has ended", ended.Message, StringComparison.Ordinal);
        Assert.Contains("There is no run", stranger.Message, StringComparison.Ordinal);
        Assert.Contains("There is no run", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("Checkpoints are off", off.Message, StringComparison.Ordinal);
    }

    // RUN-04: after a rollback, the run goes on from there.
    [Fact]
    public async Task A_run_rolled_back_to_a_checkpoint_resumes_from_it()
    {
        var kit = Kit(Checkpoints("step"), out _);
        TwoSteps(kit);
        var work = new Work("lead", "go");
        await kit.Runner.RunAsync(work, Ct);
        await kit.Runner.RollbackAsync(work.RunId, 0, ct: Ct);
        kit.Model.Reply("one again").Reply("two again");

        var result = await kit.Runner.ResumeAsync(work.RunId, ct: Ct);

        Assert.Equal((AgentOutcome.Completed, "two again"), (result.Outcome, result.Output));
        Assert.Equal(RunStatus.Completed, (await kit.Storage.Runs.ReadAsync(null, work.RunId, Ct))!.Status);
        Assert.Equal([0, 1, 2], (await kit.Storage.Checkpoints.ReadAsync(null, work.RunId, Ct)).Select(checkpoint => checkpoint.Number));
    }

    private static CheckpointOptions Checkpoints(string at) => at == "off"
        ? new()
        : new()
        {
            Enabled = true,
            At = at.Length == 0 ? null : [.. at.Split(',').Select(point => Enum.Parse<CheckpointPoint>(point, ignoreCase: true))],
        };

    /// <summary>The scripted replies of the workflow: each step records a fact and writes a file, and the second also deploys.</summary>
    private static void TwoSteps(TestKit kit) =>
        kit.Model
            .CallTools(("fact", """{ "subject": "a", "value": "1", "source": "s" }"""), ("write", """{ "path": "a.txt", "content": "one" }"""))
            .Reply("one")
            .CallTools(("fact", """{ "subject": "b", "value": "2", "source": "s" }"""), ("write", """{ "path": "b.txt", "content": "two" }"""), ("deploy", """{ "to": "prod" }"""))
            .Reply("two");

    private TestKit Kit(
        CheckpointOptions checkpoints, out FakeTool deployed, bool memory = false, Func<Task<ToolResult>>? deploy = null, InMemoryStorage? storage = null)
    {
        var options = Options(
            ("fact", new() { Source = "builtin:record.propose_fact" }),
            ("write", Extension(WorkspaceTools.Write) with { GateExemption = "Tests only." }),
            ("deploy", Extension("deploy") with { GateExemption = "Tests only.", Irreversible = true, Approval = Approval.Never }));
        var worker = options.Agents[Agent] with { Output = new() { Checks = [] }, Context = new() { History = new() { Strategy = HistoryStrategy.Full } } };
        var step = (string id) => new StepOptions { Id = id, Agent = Agent };
        options = options with
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                [Agent] = worker,
                ["lead"] = new() { Instructions = "Lead.", Pattern = new() { Type = PatternOptions.Workflow, Steps = [step("one"), step("two")] } },
            },
            Capabilities = new()
            {
                ConversationStore = new() { Enabled = true },
                Checkpoints = checkpoints,
                ProjectMemory = new() { Enabled = memory },
            },
        };
        var tool = new FakeTool(ToolKind.Write, run: async (_, _) => deploy is null ? ToolResult.Success("deployed") : await deploy());
        deployed = tool;
        var tools = new Dictionary<string, ITool>(new WorkspaceTools(call => workspace.OpenWorkingCopyAsync("w", call.Agent, Ct)).Tools) { ["deploy"] = tool };
        return new TestKit(options, tools, workspace: workspace, storage: storage);
    }
}

internal static class CheckpointTestsExtensions
{
    /// <summary>Runs the script on the kit and asks for a checkpoint at a point.</summary>
    public static async Task<Checkpoint?> AfterRunAsync(this TestKit kit, Action<TestKit> script, CheckpointPoint point)
    {
        script(kit);
        var work = new Work("lead", "go");
        await kit.Runner.RunAsync(work, TestContext.Current.CancellationToken);
        return await kit.Runner.CheckpointAsync(work.RunId, point: point, ct: TestContext.Current.CancellationToken);
    }
}
