using System.CommandLine;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Cli;

/// <summary>The <c>sof</c> command line: for now <c>config show</c> and <c>config validate</c>.</summary>
public static class SofCommandLine
{
    public static int Run(IReadOnlyList<string> args, SofEnvironment host)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);
        var shared = new ConfigurationCommandOptions();
        var config = new Command("config", "Show or check the configuration.")
        {
            ShowCommand.Create(shared, host),
            ValidateCommand.Create(shared, host),
        };
        var root = new RootCommand("sof - the Officina coding team CLI.") { config };

        var parse = root.Parse([.. args]);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                host.Error.WriteLine($"error: {error.Message}");
            }

            host.Error.WriteLine("Run sof --help for usage.");
            return ExitCodes.Usage;
        }

        try
        {
            return parse.Invoke(new InvocationConfiguration { Output = host.Out, Error = host.Error, EnableDefaultExceptionHandler = false });
        }
        catch (ConfigurationException exception)
        {
            // Names something the configuration does not have, such as an agent.
            foreach (var error in exception.Errors)
            {
                host.Error.WriteLine($"error: {error}");
            }

            return ExitCodes.Usage;
        }
    }
}
