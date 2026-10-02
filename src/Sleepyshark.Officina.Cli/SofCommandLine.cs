using System.CommandLine;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The <c>sof</c> command line: plain <c>sof</c> or <c>sof chat</c>, <c>run</c>, <c>resume</c>, <c>rollback</c>, <c>report</c>,
/// <c>config show</c>, <c>config validate</c> and <c>config dry-run</c>. In a chat session, each is typed after a <c>/</c>.
/// </summary>
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
        var parse = Create(shared, host).Parse([.. args]);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                host.Error.WriteLine($"error: {error.Message}");
            }

            host.Error.WriteLine(host.InSession ? "Type /help for the commands." : "Run sof --help for usage.");
            return Task.FromResult(ExitCodes.Usage);
        }

        // Ctrl+C cancels the run, which has run.cancelWithin to stop. System.CommandLine ends the process after its termination
        // timeout (2 seconds unless set), so that must outlast the run's. A chat session handles the signals itself, as Ctrl+C
        // there cancels the reply and keeps the session, and keeps the same timeout once the session is ending; a command typed
        // in a session is cancelled by the session.
        var chats = parse.CommandResult.Command is RootCommand || parse.CommandResult.Command.Name == "chat";
        var configuration = new InvocationConfiguration
        {
            Output = host.Out,
            Error = host.Error,
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = host.InSession || (chats && host.Signals is not null) ? null : TerminationTimeout(shared.Load(parse, host).Options),
        };
        return parse.InvokeAsync(configuration, cancel);
    }

    /// <summary>The commands, with plain <c>sof</c> starting a chat session.</summary>
    internal static RootCommand Create(ConfigurationCommandOptions shared, SofEnvironment host)
    {
        var config = new Command("config", "Show, check or dry-run the configuration.")
        {
            ShowCommand.Create(shared, host),
            ValidateCommand.Create(shared, host),
            DryRunCommand.Create(shared, host),
        };
        var root = new RootCommand("sof - the Officina coding team CLI. Plain sof starts a chat session.")
        {
            config, RunCommand.Create(shared, host), ChatCommand.Create(shared, host), ResumeCommand.CreateResume(shared, host),
            ResumeCommand.CreateRollback(shared, host), ResumeCommand.CreateReport(shared, host),
        };
        ChatCommand.AddTo(root, shared, host);
        return root;
    }

    /// <summary>How long Ctrl+C waits for the command to end before the process does.</summary>
    internal static TimeSpan TerminationTimeout(OfficinaOptions options) => options.Run.CancelWithin + TerminationMargin;
}
