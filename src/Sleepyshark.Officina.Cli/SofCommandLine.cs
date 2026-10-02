using System.CommandLine;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Cli;

/// <summary>The <c>sof</c> command line: <c>run</c>, <c>resume</c>, <c>rollback</c>, <c>report</c>, <c>config show</c>, <c>config validate</c> and <c>config dry-run</c>.</summary>
public static class SofCommandLine
{
    /// <summary>How long after <c>run.cancelWithin</c> the process is given to record a cancelled run, print its report and clean up.</summary>
    internal static readonly TimeSpan TerminationMargin = TimeSpan.FromSeconds(5);

    /// <param name="args">The command line.</param>
    /// <param name="host">The process environment.</param>
    /// <param name="cancel">Cancels the command, as Ctrl+C does; a test's stand-in for the signal.</param>
    public static Task<int> RunAsync(IReadOnlyList<string> args, SofEnvironment host, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);
        var shared = new ConfigurationCommandOptions();
        var config = new Command("config", "Show, check or dry-run the configuration.")
        {
            ShowCommand.Create(shared, host),
            ValidateCommand.Create(shared, host),
            DryRunCommand.Create(shared, host),
        };
        var root = new RootCommand("sof - the Officina coding team CLI.") { config, RunCommand.Create(shared, host), ResumeCommand.CreateResume(shared, host), ResumeCommand.CreateRollback(shared, host), ResumeCommand.CreateReport(shared, host) };

        var parse = root.Parse([.. args]);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                host.Error.WriteLine($"error: {error.Message}");
            }

            host.Error.WriteLine("Run sof --help for usage.");
            return Task.FromResult(ExitCodes.Usage);
        }

        // Ctrl+C cancels the run, which has run.cancelWithin to stop. System.CommandLine ends the process after its termination
        // timeout (2 seconds unless set), so that must outlast the run's.
        var configuration = new InvocationConfiguration
        {
            Output = host.Out,
            Error = host.Error,
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = TerminationTimeout(shared.Load(parse, host).Options),
        };
        return parse.InvokeAsync(configuration, cancel);
    }

    /// <summary>How long Ctrl+C waits for the command to end before the process does.</summary>
    internal static TimeSpan TerminationTimeout(OfficinaOptions options) => options.Run.CancelWithin + TerminationMargin;
}
