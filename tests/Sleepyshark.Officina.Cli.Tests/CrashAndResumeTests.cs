using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Storage.Sqlite;
using Sleepyshark.Officina.Testing;
using Sleepyshark.Officina.Workspace;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// A run killed at random points resumes with no lost checkpointed work and no repeated irreversible effect (RUN-04, RUN-07,
/// TEST-12, TEST-23), on real SQLite and a real git repository. A process that dies is a runner that is never heard from
/// again: it stops at the point under test, its lock on the workspace is released as the operating system would release it,
/// and a new runner, storage and workspace take over. The model provider and the tool that calls out are the stand-ins.
/// The workflow has two steps, both turns of <c>dev</c>, who keeps its conversation: the first records a fact and writes a
/// file, the second does the same and then deploys, which is irreversible.
/// </summary>
public sealed class CrashAndResumeTests : IDisposable
{
    private readonly Sof sof = new();
    private readonly FakeTimeProvider time = new();
    private readonly CancellationTokenSource processes = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Database => Path.Combine(sof.Directory, ".sof", "sof.db");

    public void Dispose()
    {
        processes.Dispose();
        sof.Dispose();
    }

    // The points a run dies at: before any model call; after the first step with the second not begun; in the middle of the
    // deploy, after its intent and before its outcome; and after the deploy.
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(3, false, false)]
    [InlineData(0, true, true)]
    [InlineData(4, false, true)]
    public async Task A_run_killed_at_a_point_resumes_without_losing_checkpointed_work_or_repeating_the_deploy(int dieAtModelCall, bool dieInDeploy, bool deployAttempted)
    {
        sof.Write("README.md", "A project.\n").Commit();
        var work = new Work("lead", "go");
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deployedBeforeTheCrash = new FakeTool(ToolKind.Write, run: async (_, token) =>
        {
            if (dieInDeploy)
            {
                arrived.TrySetResult();
                await Task.Delay(Timeout.Infinite, token); // the process dies here, after the intent and before the outcome
            }

            return ToolResult.Success("deployed");
        });
        var first = await StartAsync(work.RunId, new DyingModel(Script(), dieAtModelCall, arrived), deployedBeforeTheCrash);
        var zombie = first.Runner.RunAsync(work, processes.Token);
        await arrived.Task.WaitAsync(Ct);

        // A new process: nothing after this is heard from the first.
        first.Workspace.Dispose();
        var before = await first.Storage.Records.ReadAsync(null, work.RunId, Ct);
        var last = (await first.Storage.Checkpoints.ReadAsync(null, work.RunId, Ct))[^1];
        var deployedAfterTheRestart = new FakeTool(ToolKind.Write);
        var second = await StartAsync(work.RunId, Script(), deployedAfterTheRestart);
        var result = await second.Runner.ResumeAsync(work.RunId, ct: Ct);

        // At most once: a deploy attempted before the crash is never repeated, and the call goes to a human.
        Assert.Equal(1, deployedBeforeTheCrash.Calls.Count + deployedAfterTheRestart.Calls.Count);
        Assert.Equal(
            deployAttempted ? (AgentOutcome.HandedOff, HandoffReason.RoutedByGate, RunStatus.WaitingForHuman) : (AgentOutcome.Completed, null, RunStatus.Completed),
            (result.Outcome, result.Handoff?.Reason, (await second.Storage.Runs.ReadAsync(null, work.RunId, Ct))!.Status));

        // RUN-07: the call whose outcome is unknown is flagged.
        var resumed = (await second.Storage.Events.ReadAsync(null, work.RunId, 0, Ct)).Select(coreEvent => coreEvent.Payload).OfType<RunResumed>().Single();
        Assert.Equal(dieInDeploy ? ["deploy"] : [], resumed.Interrupted.Select(effect => effect.Tool));

        // No lost checkpointed work: the record up to the last checkpoint is as it was, and so are the working copy's files.
        // Known gap (part 2): a pattern resumes from its first step, so work after step one's checkpoint is redone and so is step
        // one itself, which records its fact again as an entry of its own; only the checkpointed prefix is compared.
        var after = await second.Storage.Records.ReadAsync(null, work.RunId, Ct);
        Assert.Equal(before.Take((int)last.Record), after.Take((int)last.Record));
        Assert.Equal(last.Record > 0, last.Workspace.Count == 1);
        if (last.Workspace.Count == 1)
        {
            Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(second.Workspace.OpenCopies.Single().Directory, "a.txt"), Ct));
        }

        await processes.CancelAsync();
        await zombie;
        second.Workspace.Dispose();
    }

    // RUN-08: a conversation is shared by every run of the agent, so a run is not resumed over another run's turns.
    [Fact]
    public async Task A_run_is_not_resumed_over_turns_another_run_wrote_to_the_same_conversation()
    {
        sof.Write("README.md", "A project.\n").Commit();
        var work = new Work("lead", "go");
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await StartAsync(work.RunId, new DyingModel(Script(), 3, arrived), new FakeTool(ToolKind.Write));
        var zombie = first.Runner.RunAsync(work, processes.Token);
        await arrived.Task.WaitAsync(Ct);
        first.Workspace.Dispose();
        var other = new Work("dev", "other work");
        var between = await StartAsync(other.RunId, new ScriptedModelProvider().Reply("done"), new FakeTool(ToolKind.Write));
        await between.Runner.RunAsync(other, Ct);
        between.Workspace.Dispose();
        var turns = await between.Storage.Conversations.CountAsync(null, "dev", null, Ct);

        var second = await StartAsync(work.RunId, Script(), new FakeTool(ToolKind.Write));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => second.Runner.ResumeAsync(work.RunId, ct: Ct));
        var rolledBack = await Assert.ThrowsAsync<InvalidOperationException>(() => second.Runner.RollbackAsync(work.RunId, 1, ct: Ct));

        Assert.Contains("Another run has written to dev's conversation since checkpoint 1", refused.Message, StringComparison.Ordinal);
        Assert.Equal(refused.Message, rolledBack.Message);
        Assert.Equal((turns, RunStatus.Running), (await second.Storage.Conversations.CountAsync(null, "dev", null, Ct), (await second.Storage.Runs.ReadAsync(null, work.RunId, Ct))!.Status));
        await processes.CancelAsync();
        await zombie;
        second.Workspace.Dispose();
    }

    private static ScriptedModelProvider Script() =>
        new ScriptedModelProvider()
            .CallTools(("fact", """{ "subject": "a", "value": "1", "source": "s" }"""), ("write", """{ "path": "a.txt", "content": "one" }"""))
            .Reply("one")
            .CallTools(("fact", """{ "subject": "b", "value": "2", "source": "s" }"""), ("write", """{ "path": "b.txt", "content": "two" }"""), ("deploy", """{ "to": "prod" }"""))
            .Reply("two");

    private sealed record Process(AgentRunner Runner, IStorage Storage, GitWorkspace Workspace);

    private async Task<Process> StartAsync(string runId, IModelProvider model, ITool deploy)
    {
        var options = new OfficinaOptions
        {
            Run = new() { PermissionMode = PermissionMode.Auto },
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["dev"] = new()
                {
                    Instructions = "Work.", Tools = ["all"], Output = new() { Checks = [] }, Context = new() { History = new() { Strategy = HistoryStrategy.Full } },
                },
                ["lead"] = new()
                {
                    Instructions = "Lead.",
                    Pattern = new()
                    {
                        Type = PatternOptions.Workflow, Steps = [new StepOptions { Id = "one", Agent = "dev" }, new StepOptions { Id = "two", Agent = "dev" }],
                    },
                },
            },
            Tools = new Dictionary<string, ToolOptions>
            {
                ["fact"] = new() { Source = "builtin:record.propose_fact" },
                ["write"] = new() { Source = "extension:workspace.write_file", GateExemption = "Tests only." },
                ["deploy"] = new() { Source = "extension:deploy", GateExemption = "Tests only.", Irreversible = true, Approval = Approval.Never },
            },
            ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["all"] = ["fact", "write", "deploy"] },
            Capabilities = new()
            {
                ConversationStore = new() { Enabled = true },
                Workspace = new() { Enabled = true },
                Checkpoints = new() { Enabled = true, At = [CheckpointPoint.Step] },
            },
        };
        var workspace = await GitWorkspace.OpenAsync(sof.Directory, runId, options.Capabilities.Workspace, new Dictionary<string, ICheck>(), time, Ct);
        IStorage storage = await SqliteStorage.OpenAsync(Database, Ct); // in the workspace's state folder

        // As sof does when it opens the workspace: a run that can resume keeps its branches.
        await workspace.RemoveLeftoversAsync(task => Task.FromResult(task.StartsWith(runId, StringComparison.Ordinal)), null, Ct);
        var tools = new Dictionary<string, ITool>(new WorkspaceTools(async agent => await workspace.OpenWorkingCopyAsync($"{runId}-{agent}", agent, Ct)).Tools)
        {
            ["deploy"] = deploy,
        };
        var runner = new AgentRunner(
            options, options.Providers.Keys.ToDictionary(name => name, _ => model), storage, tools, new Dictionary<string, IGate>(), new Dictionary<string, ICheck>(),
            new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(), new InMemorySecretSource(new Dictionary<string, string>()), time, workspace: workspace);
        return new Process(runner, storage, workspace);
    }

    /// <summary>A model that stops answering at one of its calls, as a process that has died does.</summary>
    private sealed class DyingModel(ScriptedModelProvider inner, int dieAt, TaskCompletionSource arrived) : IModelProvider
    {
        private int calls;

        public ProviderCapabilities CapabilitiesOf(string model) => inner.CapabilitiesOf(model);

        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == dieAt)
            {
                arrived.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            await foreach (var modelEvent in inner.StreamAsync(request, ct))
            {
                yield return modelEvent;
            }
        }
    }
}
