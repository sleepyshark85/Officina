using System.Diagnostics;
using System.Text;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Workspace;

/// <summary>Runs the git CLI, which the workspace uses because libgit2's worktree support is limited (DESIGN.md §7).</summary>
internal static class Git
{
    /// <summary>Runs git and returns its output.</summary>
    /// <exception cref="InvalidOperationException">Git failed.</exception>
    public static async Task<string> RunAsync(string directory, CancellationToken ct, params string[] arguments)
    {
        var (exitCode, output, error) = await TryRunAsync(directory, ct, arguments).ConfigureAwait(false);
        return exitCode == 0 ? output : throw new InvalidOperationException($"git {arguments[0]} failed in {directory}: {error.Trim()}");
    }

    /// <summary>Runs git and returns its exit code, for commands whose failure is an answer, such as a rebase that conflicts.</summary>
    public static async Task<(int ExitCode, string Output, string Error)> TryRunAsync(string directory, CancellationToken ct, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--no-pager");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Rebased commits get the workspace as committer, so no git identity needs to be configured; never prompt.
        start.Environment["GIT_COMMITTER_NAME"] = "Officina";
        start.Environment["GIT_COMMITTER_EMAIL"] = "officina@localhost";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
