using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Reports;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tasks;
using Sleepyshark.Officina.Storage.Sqlite;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// TEST-29, the team simulation: a scripted team takes a small goal through plan, parallel work, review, integration, a forced
/// restart and a report, entirely offline, on a real git repository and real SQLite, wired as <c>sof</c> wires a run. The lead
/// plans two tasks, each a file; two developers write them at once in their tasks' working copies and submit them, which runs
/// the tests there; the reviewer approves each, and each is integrated, the build and the tests passing on the baseline. The
/// process dies while the second task is reviewed, after the first is integrated; a new process resumes the run from its last
/// checkpoint and finishes it. The model, the sandbox (every command passes) and the clock are the stand-ins.
/// </summary>
public sealed class TeamSimulationTests : IDisposable
{
    private readonly Sof sof = new();
    private readonly FakeTimeProvider time = new();
    private readonly FakeSandbox sandbox = new() { Answer = _ => ("ok", 0) };
    private static readonly string[] Tasks = ["a", "b"];

    private readonly CancellationTokenSource processes = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        processes.Dispose();
        sof.Dispose();
    }

    [Fact]
    public async Task A_scripted_team_plans_works_in_parallel_reviews_integrates_survives_a_restart_and_reports()
    {
        sof.Write("README.md", "A calculator.\n").Commit();
        var work = new Work("team", "Write the parser and the printer.");
        var dying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // It dies in the second review, whichever task the developers submitted first.
        string? reviewed = null;
        var first = await StartAsync(work.RunId, new DyingModel(Script(), request => Is(request, "Review task ") && (reviewed ??= Reviewed(request)) != Reviewed(request), dying), leaveWorkingCopies: true);
        var zombie = first.Runner.RunAsync(work, processes.Token);
        await dying.Task.WaitAsync(Ct);

        // The process dies: nothing after this is heard from it, and the operating system releases its hold on the workspace.
        await first.Host.DisposeAsync();
        var resumed = Script(plan: false, develop: false, first: reviewed);
        var second = await StartAsync(work.RunId, resumed, leaveWorkingCopies: false);
        var result = await second.Runner.ResumeAsync(work.RunId, ct: Ct);

        Assert.Equal((AgentOutcome.Completed, "The parser and the printer are in."), (result.Outcome, result.Output));
        Assert.All(await second.Runner.Board(null, work.RunId).ReadAsync(Ct), task => Assert.Equal(TaskState.Done, task.State));

        // Both changes are on the baseline, each as one commit attributed to its task, and the copies are gone with the run.
        Assert.Equal(("1 + 2", "3"), (await Git("show", "main:parser.txt"), await Git("show", "main:printer.txt")));
        Assert.Equal(["Start", "Task a", "Task b"], (await Git("log", "--format=%s", "main")).Split('\n').Order(StringComparer.Ordinal));
        await second.Host.DisposeAsync();
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(sof.Directory, ".sof", "worktrees")));

        // A task is reviewed once its checks pass at submit, and the reviewer, who runs no commands, is told so.
        Assert.Contains(resumed.Requests, request => Is(request, "Review task ") && Is(request, "Its checks passed at submit: tests."));

        // Each task's tests ran in its own working copy (TASK-05), and the build and the tests on the baseline before each integration (WS-02).
        var commands = sandbox.Processes.Select(process => (process.Command.CommandLine, Path.GetFileName(process.Command.Directory))).ToList();
        Assert.Contains(("dotnet test", $"{work.RunId}-task.a"), commands);
        Assert.Contains(("dotnet test", $"{work.RunId}-task.b"), commands);
        Assert.Equal(4, commands.Count(command => command.Item2 == "integration"));

        // One run across the restart: its checkpoints, the integration among them, its resume, and its report.
        var events = await second.Storage.Events.ReadAsync(null, work.RunId, 0, Ct);
        Assert.Single(events, coreEvent => coreEvent.Payload is RunResumed);
        Assert.Contains(events, coreEvent => coreEvent.Payload is CheckpointTaken { Point: CheckpointPoint.Integration });
        var report = await RunReport.BuildAsync(second.Storage, null, work.RunId, Ct);
        Assert.Equal((RunStatus.Completed, 1, 0), (report!.Status, report.Resumes, report.OpenIssues.Count));
        Assert.Equal(["developer", "lead", "reviewer"], report.Cost.ByDefinition.Keys.Order(StringComparer.Ordinal));

        await processes.CancelAsync();
        await zombie;
    }

    private static bool Is(ModelRequest request, string work) => ScriptedModelProvider.WorkOf(request).Contains(work, StringComparison.Ordinal);

    /// <summary>The task a review is of, as its work begins.</summary>
    private static string Reviewed(ModelRequest request) => ScriptedModelProvider.WorkOf(request).Split(',')[0];

    /// <summary>The team's replies, each agent's by its work; a process that resumes needs only what is left.</summary>
    /// <param name="plan">Whether the plan is still to make.</param>
    /// <param name="develop">Whether the tasks are still to do.</param>
    /// <param name="first">The review done before the restart, as its work begins, which is not asked for again.</param>
    private static ScriptedModelProvider Script(bool plan = true, bool develop = true, string? first = null)
    {
        var model = new ScriptedModelProvider();
        if (plan)
        {
            model.When(request => Is(request, "You lead a team")).CallTools(
                ("create", """{ "id": "a", "title": "The parser", "role": "developer", "checks": ["tests"], "requiresReview": true, "reason": "plan" }"""),
                ("create", """{ "id": "b", "title": "The printer", "role": "developer", "checks": ["tests"], "requiresReview": true, "reason": "plan" }"""))
                .Reply("Planned.");
        }

        if (develop)
        {
            foreach (var (task, file, text) in new[] { ("a", "parser.txt", "1 + 2"), ("b", "printer.txt", "3") })
            {
                model.When(request => Is(request, $"Do task {task},"))
                    .CallTools(("write", $$"""{ "path": "{{file}}", "content": "{{text}}" }""")).CallTools(("submit", $$"""{ "id": "{{task}}" }""")).Reply("Submitted.");
            }
        }

        foreach (var task in Tasks.Where(task => first != $"Review task {task}"))
        {
            model.When(request => Is(request, $"Review task {task},"))
                .CallTools(("review", $$"""{ "id": "{{task}}", "approved": true, "reasons": "It does what the task asks." }""")).Reply("Approved.");
        }

        model.When(request => Is(request, "Every task is done")).Reply("The parser and the printer are in.");
        return model;
    }

    private sealed record Process(AgentRunner Runner, IStorage Storage, WorkspaceHost Host);

    /// <summary>A process of <c>sof</c>: the configuration, the storage in the project's state folder, the workspace host, and a runner.</summary>
    private async Task<Process> StartAsync(string runId, IModelProvider model, bool leaveWorkingCopies)
    {
        var options = new OfficinaOptions
        {
            Run = new() { PermissionMode = PermissionMode.Auto },
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["team"] = new()
                {
                    Instructions = "A team.",
                    Pattern = new()
                    {
                        Type = PatternOptions.Team, Lead = "lead", MaxParallel = 3,
                        Roles = new Dictionary<string, RoleOptions> { ["developer"] = new() { Max = 2 }, ["reviewer"] = new() },
                    },
                },
                ["lead"] = new() { Instructions = "Lead.", Tools = ["lead"] },
                ["developer"] = new() { Instructions = "Develop.", Tools = ["developer"] },
                ["reviewer"] = new() { Instructions = "Review.", Tools = ["reviewer"] },
            },
            Tools = new Dictionary<string, ToolOptions>
            {
                ["create"] = new() { Source = "builtin:tasks.create" },
                ["submit"] = new() { Source = "builtin:tasks.submit_for_review" },
                ["review"] = new() { Source = "builtin:tasks.review" },
                ["write"] = new() { Source = "extension:workspace.write_file", GateExemption = "The task's change is reviewed and checked before it is integrated." },
                ["read"] = new() { Source = "extension:workspace.read_file" },
            },
            ToolSets = new Dictionary<string, IReadOnlyList<string>>
            {
                ["lead"] = ["create"], ["developer"] = ["write", "read", "submit"], ["reviewer"] = ["read", "review"],
            },
            Checks = new Dictionary<string, CheckOptions> { ["build"] = new() { Command = "dotnet build" }, ["tests"] = new() { Command = "dotnet test" } },
            Capabilities = new()
            {
                TaskBoard = new() { Enabled = true }, Team = new() { Enabled = true },
                Workspace = new() { Enabled = true, BaselineChecks = ["build", "tests"] }, Sandbox = new() { Enabled = true },
                ConversationStore = new() { Enabled = true }, Checkpoints = new() { Enabled = true, At = [CheckpointPoint.Turn, CheckpointPoint.Integration] },
            },
        };
        Assert.Empty(options.Validate());
        var host = await WorkspaceHost.OpenAsync(
            options, sof.Directory, runId, sandbox, time, task => Task.FromResult(task.StartsWith(runId, StringComparison.Ordinal)), leaveWorkingCopies, TextWriter.Null, Ct);
        IStorage storage = await SqliteStorage.OpenAsync(Path.Combine(sof.Directory, ".sof", "sof.db"), Ct); // in the workspace's state folder
        var runner = new AgentRunner(
            options, options.Providers.Keys.ToDictionary(name => name, _ => model), storage, host.Tools, host.Gates, host.Checks,
            new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(), new InMemorySecretSource(new Dictionary<string, string>()), time, workspace: host);
        return new Process(runner, storage, host);
    }

    private async Task<string> Git(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = sof.Directory, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var git = System.Diagnostics.Process.Start(start)!;
        var output = await git.StandardOutput.ReadToEndAsync(Ct);
        await git.WaitForExitAsync(Ct);
        return output.Trim();
    }

    /// <summary>A model that stops answering at the first request that matches, as a process that has died does.</summary>
    private sealed class DyingModel(ScriptedModelProvider inner, Func<ModelRequest, bool> diesAt, TaskCompletionSource dying) : IModelProvider
    {
        public ProviderCapabilities CapabilitiesOf(string model) => inner.CapabilitiesOf(model);

        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            if (diesAt(request))
            {
                dying.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            await foreach (var modelEvent in inner.StreamAsync(request, ct))
            {
                yield return modelEvent;
            }
        }
    }
}
