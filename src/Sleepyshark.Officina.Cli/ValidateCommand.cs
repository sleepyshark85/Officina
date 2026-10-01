using System.CommandLine;

namespace Sleepyshark.Officina.Cli;

/// <summary><c>sof config validate</c>: validates the configuration in full and lists every error (CFG-06).</summary>
internal static class ValidateCommand
{
    public static Command Create(ConfigurationCommandOptions shared, SofEnvironment host)
    {
        var command = new Command("validate", "Validate the configuration; exits with 1 when it has errors.");
        shared.AddTo(command);
        command.SetAction(parse =>
        {
            var configuration = shared.Load(parse, host);
            if (ConfigurationCommandOptions.ReportErrors(configuration, host) is var code && code != ExitCodes.Success)
            {
                return code;
            }

            host.Out.WriteLine("The configuration is valid.");
            return ExitCodes.Success;
        });
        return command;
    }
}
