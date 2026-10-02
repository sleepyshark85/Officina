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

    // RUN-11: the report of a stored run is shown when it ends, and later on request.
    [Fact]
    public async Task The_report_of_a_run_is_shown_when_it_ends_and_again_on_request()
    {
        model.Reply("Done.");
        var (_, ran, _) = await sof.RunAsync("run", "--input", "Greet.");
        var runId = RunId().Match(ran).Groups[1].Value;

        var (code, report, _) = await sof.RunAsync("report", runId);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains($"Run {runId}: Completed, Completed\n", report, StringComparison.Ordinal);
        Assert.Contains($"Run {runId}: Completed, Completed\n", ran, StringComparison.Ordinal);
        Assert.Contains("Work: Greet.\n", report, StringComparison.Ordinal);
    }

    // REL-04: a database in another format version is an error line, not a stack trace.
    [Theory]
    [InlineData("resume", "")]
    [InlineData("rollback", "--to")]
    [InlineData("rollback", "")]
    [InlineData("report", "")]
    [InlineData("run", "--input")]
    public async Task A_database_in_another_format_version_is_an_error_line(string command, string option)
    {
        model.Reply("Done.");
        var (_, ran, _) = await sof.RunAsync("run", "--input", "Greet.");
        var runId = RunId().Match(ran).Groups[1].Value;
        var database = Path.Combine(sof.Directory, ".sof", "sof.db");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var older = connection.CreateCommand();
            older.CommandText = "PRAGMA user_version = 4";
            await older.ExecuteNonQueryAsync(Ct);
        }

        var arguments = new List<string> { command };
        arguments.AddRange(option switch { "--to" => [runId, "--to", "0"], "--input" => ["--input", "Again."], _ => [runId] });
        var (code, _, error) = await sof.RunAsync([.. arguments]);

        Assert.Equal(ExitCodes.Invalid, code);
        Assert.StartsWith("error: ", error, StringComparison.Ordinal);
        Assert.Contains("format version 4", error, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", error, StringComparison.Ordinal);
    }

    // RUN-04: a run that another process still holds is not resumed or rolled back from here.
    [Fact]
    public async Task A_run_held_by_another_process_is_not_resumed_or_rolled_back()
    {
        model.Reply("Done.");
        var (_, ran, _) = await sof.RunAsync("run", "--input", "Greet.");
        var runId = RunId().Match(ran).Groups[1].Value;
        await using var held = new FileStream(Path.Combine(sof.Directory, ".sof", $"{runId}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var (resumeCode, _, resumeError) = await sof.RunAsync("resume", runId);
        var (rollbackCode, _, rollbackError) = await sof.RunAsync("rollback", runId, "--to", "0");

        Assert.Equal((ExitCodes.Invalid, ExitCodes.Invalid), (resumeCode, rollbackCode));
        Assert.Contains("is held by another process", resumeError, StringComparison.Ordinal);
        Assert.Contains("is held by another process", rollbackError, StringComparison.Ordinal);
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
