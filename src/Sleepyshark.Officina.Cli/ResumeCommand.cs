using System.CommandLine;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Storage.Sqlite;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// <c>sof resume &lt;run&gt;</c>: starts a run that stopped without ending, such as after a crash, again from its last
/// checkpoint, with you at the console as for <c>sof run</c> (RUN-04). <c>sof rollback &lt;run&gt; [--to &lt;checkpoint&gt;]</c>:
/// lists a run's checkpoints, or returns the run to one, restoring its state and working copies together and listing the
/// effects it cannot undo (RUN-08). A rolled-back run is resumed from there.
/// </summary>
internal static class ResumeCommand
{
    public static Command CreateResume(ConfigurationCommandOptions shared, SofEnvironment host)
    {
        var run = new Argument<string>("run") { Description = "The run's id." };
        var command = new Command(
            "resume", "Continue a run that stopped without ending, from its last checkpoint. The run budget starts again from zero. Do not resume a run that is still running in another terminal.") { run };
        shared.AddTo(command);
        command.SetAction((parse, ct) =>
        {
            var runId = parse.GetValue(run)!;
            return RunCommand.ExecuteAsync(parse, shared, host, runId, agentName: null, existing: true, leaveWorkingCopies: false, async (session, token) =>
            {
                try
                {
                    return await RunCommand.RunAsync(session, () => session.Runner.ResumeAsync(runId, ct: token), host, token);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    host.Error.WriteLine($"error: {exception.Message}");
                    return ExitCodes.Invalid;
                }
            }, ct);
        });
        return command;
    }

    public static Command CreateRollback(ConfigurationCommandOptions shared, SofEnvironment host)
    {
        var run = new Argument<string>("run") { Description = "The run's id." };
        var to = new Option<int?>("--to") { Description = "The checkpoint to go back to (default: list the run's checkpoints)." };
        var command = new Command("rollback", "Go back to one of a run's checkpoints, or list them.") { run, to };
        shared.AddTo(command);
        command.SetAction((parse, ct) =>
        {
            var runId = parse.GetValue(run)!;
            var number = parse.GetValue(to);
            if (number is null)
            {
                return ListAsync(shared.Directory(parse, host), runId, host, ct);
            }

            // The working copies stay: the run goes on from the checkpoint with `sof resume`.
            return RunCommand.ExecuteAsync(parse, shared, host, runId, agentName: null, existing: true, leaveWorkingCopies: true, async (session, token) =>
            {
                try
                {
                    return Report(session, await session.Runner.RollbackAsync(runId, number.Value, ct: token));
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    host.Error.WriteLine($"error: {exception.Message}");
                    return ExitCodes.Invalid;
                }
            }, ct);
        });
        return command;
    }

    /// <summary>Lists a run's checkpoints from the store alone: no tool servers, no lock on the workspace, nothing cleaned up.</summary>
    private static async Task<int> ListAsync(string directory, string runId, SofEnvironment host, CancellationToken ct)
    {
        var database = Path.Combine(directory, WorkspaceOptions.StateFolder, "sof.db");
        IStorage? storage = File.Exists(database) ? await SqliteStorage.OpenAsync(database, ct) : null;
        if (storage is null || await storage.Runs.ReadAsync(null, runId, ct) is null)
        {
            host.Error.WriteLine($"error: there is no run {runId}.");
            return ExitCodes.Invalid;
        }

        foreach (var checkpoint in await storage.Checkpoints.ReadAsync(null, runId, ct))
        {
            host.Out.WriteLine($"{checkpoint.Number}  {checkpoint.Time:u}  {checkpoint.Point}");
        }

        return ExitCodes.Success;
    }

    private static int Report(RunCommand.Session session, RollbackReport report)
    {
        session.Output.WriteLine($"Run {session.RunId} is back at checkpoint {report.To.Number}. Continue it with: sof resume {session.RunId}");
        if (report.NotUndone.Count > 0)
        {
            session.Output.WriteLine("Not undone, because it is outside the run's state:");
            foreach (var effect in report.NotUndone)
            {
                session.Output.WriteLine(
                    $"  {effect.Agent} called {effect.Tool} {effect.Arguments}{(effect.Irreversible ? " (irreversible)" : "")}{(effect.Finished ? "" : ", outcome unknown")}");
            }
        }

        if (report.MemoryChanges.Count > 0)
        {
            session.Output.WriteLine($"Project memory is not rolled back: it has {report.MemoryChanges.Count} changes since the checkpoint.");
        }

        return ExitCodes.Success;
    }
}
