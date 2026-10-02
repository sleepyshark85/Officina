using System.CommandLine;

namespace Sleepyshark.Officina.Cli;

/// <summary>The <c>sof</c> command line: <c>run</c>, <c>resume</c>, <c>rollback</c>, <c>report</c>, <c>config show</c>, <c>config validate</c> and <c>config dry-run</c>.</summary>
public static class SofCommandLine
{
    public static Task<int> RunAsync(IReadOnlyList<string> args, SofEnvironment host)
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

        return parse.InvokeAsync(new InvocationConfiguration { Output = host.Out, Error = host.Error, EnableDefaultExceptionHandler = false });
    }
}
