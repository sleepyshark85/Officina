using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Storage.Sqlite;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary><c>sof resume</c> and <c>sof rollback</c>, in a real git repository with the run stored in <c>.sof/sof.db</c> (RUN-04, RUN-08).</summary>
public sealed partial class ResumeCommandTests : IDisposable
{
    private const string Configuration = """
        {
          "run": { "permissionMode": "auto" },
          "agents": { "dev": { "instructions": "Work.", "tools": ["all"] } },
          "tools": {
            "write": { "source": "extension:workspace.write_file", "gateExemption": "Writes only to the agent's working copy.", "approval": "never" },
            "run": { "source": "extension:sandbox.run", "gates": ["commands"] }
          },
          "gates": { "commands": { "use": "extension:sandbox.commandRules" } },
          "toolSets": { "all": ["write", "run"] },
          "capabilities": {
            "conversationStore": { "enabled": true },
            "checkpoints": { "enabled": true },
            "workspace": { "enabled": true },
            "sandbox": { "enabled": true, "commandRules": [{ "match": "dotnet*", "action": "allow" }] }
          }
        }
        """;

    private readonly Sof sof = new();
    private readonly ScriptedModelProvider model = new();

    public ResumeCommandTests()
    {
        sof.Write("sof.json", Configuration).Commit();
        sof.Providers["claude"] = model;
        sof.Sandbox = new FakeSandbox().Reply("built", 0);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => sof.Dispose();

    [Fact]
    public async Task A_run_is_rolled_back_to_a_checkpoint_with_its_working_copy_and_then_resumes_from_it()
    {
        model.CallTools(("write", """{ "path": "hello.txt", "content": "hi" }"""), ("run", """{ "command": "dotnet build" }""")).Reply("Done.");
        var (_, ran, _) = await sof.RunAsync("run", "--input", "Greet.");
        var runId = RunId().Match(ran).Groups[1].Value;
        var copy = Path.Combine(sof.Directory, ".sof", "worktrees", $"{runId}-dev");
        Assert.False(Directory.Exists(copy)); // a run that ends removes its working copies

        // The run's checkpoints: its start, and the end of its turn.
        var (listCode, listing, _) = await sof.RunAsync("rollback", runId);
        Assert.Equal(ExitCodes.Success, listCode);
        Assert.Matches(@"^0  .*  Start\n1  .*  Turn\n$", listing);

        // Back to the turn's end: the working copy returns with its file, and the command lists what it cannot undo (RUN-08).
        var (_, atTurn, _) = await sof.RunAsync("rollback", runId, "--to", "1");
        Assert.Contains($"Run {runId} is back at checkpoint 1.", atTurn, StringComparison.Ordinal);
        Assert.Equal("hi", await File.ReadAllTextAsync(Path.Combine(copy, "hello.txt"), Ct));

        // Back to the start: the file is gone, and the command it ran is listed, not undone.
        var (code, atStart, _) = await sof.RunAsync("rollback", runId, "--to", "0");
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains($"Run {runId} is back at checkpoint 0.", atStart, StringComparison.Ordinal);
        Assert.Contains("Not undone, because it is outside the run's state:\n  dev called run", atStart, StringComparison.Ordinal);
        Assert.Contains("dotnet build", atStart, StringComparison.Ordinal);
        Assert.False(Directory.Exists(copy)); // the start had no working copy

        // The run goes on from there, once.
        model.CallTools(("write", """{ "path": "hello.txt", "content": "hi again" }""")).Reply("Done again.");
        var (resumeCode, resumed, _) = await sof.RunAsync("resume", runId);
        Assert.Equal(ExitCodes.Success, resumeCode);
        Assert.Contains("dev: Completed, cost $0.00\nDone again.\n", resumed, StringComparison.Ordinal);
        var (endedCode, _, ended) = await sof.RunAsync("resume", runId);
        Assert.Equal((ExitCodes.Invalid, true), (endedCode, ended.Contains("has ended (Completed)", StringComparison.Ordinal)));
    }

    // RUN-09: a rollback from a new process continues the run's event numbering, so the run reads in order.
    [Fact]
    public async Task A_rollback_in_a_new_process_stores_its_event_with_the_next_sequence_number()
    {
        model.Reply("Done.");
        var (_, ran, _) = await sof.RunAsync("run", "--input", "Greet.");
        var runId = RunId().Match(ran).Groups[1].Value;
        IStorage Open() => SqliteStorage.OpenAsync(Path.Combine(sof.Directory, ".sof", "sof.db"), Ct).GetAwaiter().GetResult();
        var last = (await Open().Events.ReadAsync(null, runId, 0, Ct))[^1].Sequence;

        var (code, _, _) = await sof.RunAsync("rollback", runId, "--to", "0");

        var events = await Open().Events.ReadAsync(null, runId, 0, Ct);
        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal((last + 1, true), (events[^1].Sequence, events[^1].Payload is Core.Events.RunRolledBack));
        Assert.Equal(events.Count, events.Select(coreEvent => coreEvent.Sequence).Distinct().Count());
    }

    [Fact]
    public async Task A_run_that_is_not_stored_cannot_be_resumed_or_rolled_back()
    {
        var (resumeCode, _, resumeError) = await sof.RunAsync("resume", "nobody");
        var (rollbackCode, _, rollbackError) = await sof.RunAsync("rollback", "nobody", "--to", "0");

        Assert.Equal((ExitCodes.Invalid, "error: there is no run nobody.\n"), (resumeCode, resumeError));
        Assert.Equal((ExitCodes.Invalid, "error: there is no run nobody.\n"), (rollbackCode, rollbackError));
    }

    // RUN-03: the owner takes a checkpoint at the console.
    [Fact]
    public async Task The_owner_takes_a_checkpoint_at_the_console()
    {
        model.CallTools(("run", """{ "command": "dotnet build" }""")).Reply("Done.");
        sof.Sandbox = new FakeSandbox().Reply("building", exitCode: null); // the command does not end until the run is cancelled

        var run = sof.RunAsync("run", "--input", "Build.");
        await sof.Sandbox.FirstStarted.WaitAsync(Ct);
        sof.In.Type("checkpoint");
        await sof.Out.WaitForAsync("checkpoint 1 taken.", Ct);
        sof.In.Type("cancel");
        var (exitCode, _, _) = await run;

        Assert.Equal(ExitCodes.NotCompleted, exitCode);
    }

    [GeneratedRegex(@"^run (\S+)$", RegexOptions.Multiline)]
    private static partial Regex RunId();
}
