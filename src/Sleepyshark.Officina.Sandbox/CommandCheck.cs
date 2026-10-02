using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Sandbox;

/// <summary>
/// A check that runs a command, such as a build or the tests, in the sandbox in the working copy it checks: a task's, when the
/// task is submitted (TASK-05), or the change applied to the baseline, before it is integrated (WS-02). It passes when the
/// command exits with 0; its last lines of output are the findings. Only that decides, whatever an agent says (INV-09). The
/// command is the owner's configuration, so no command rule applies to it; it gets no secrets, and the network the agents' commands get.
/// </summary>
/// <param name="sandbox">Where the command runs.</param>
/// <param name="options">The sandbox settings: the hosts it may reach and the toolchains.</param>
/// <param name="workspace">The workspace settings, whose protected paths the command cannot reach (WS-05).</param>
/// <param name="commandLine">The command.</param>
public sealed class CommandCheck(ISandbox sandbox, SandboxOptions options, WorkspaceOptions workspace, string commandLine) : ICheck
{
    /// <summary>How many of the last lines of output a failure reports.</summary>
    private const int FindingLines = 20;

    public async ValueTask<CheckResult> RunAsync(CheckContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Directory is not { } directory)
        {
            return new(false, ["there is no working copy to run the command in"]);
        }

        var (hidden, readOnly) = ProtectedPaths.Find(directory, workspace);
        await using var process = await sandbox.StartAsync(
            new SandboxCommand(commandLine, directory, SandboxLimits.Default, options.AllowedHosts, new Dictionary<string, string>(), hidden, readOnly, options.Toolchains), ct)
            .ConfigureAwait(false);
        var last = new Queue<string>();
        await foreach (var line in process.Output.ReadAllAsync(ct).ConfigureAwait(false))
        {
            last.Enqueue(line);
            if (last.Count > FindingLines)
            {
                last.Dequeue();
            }
        }

        var exitCode = await process.ExitCode.WaitAsync(ct).ConfigureAwait(false);
        return exitCode == 0 ? new(true, []) : new(false, [$"{commandLine} exited with code {exitCode}", .. last]);
    }
}
