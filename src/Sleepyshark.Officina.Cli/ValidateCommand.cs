using System.CommandLine;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;

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

            // What sof run refuses as it starts: extensions sof does not register, and what the models' providers lack. No model is
            // called and nothing is opened.
            var (providers, claude) = RunCommand.Providers(configuration.Options, host, new EnvironmentSecrets(host.Variables));
            using (claude)
            {
                IReadOnlyList<ConfigurationError> errors =
                    [.. WorkspaceHost.RegistrationErrors(configuration.Options), .. AgentRunner.ProviderErrors(configuration.Options, providers, new Dictionary<string, IHistoryShortener>())];
                if (errors.Count > 0)
                {
                    return ConfigurationCommandOptions.ReportErrors(errors, host);
                }
            }

            host.Out.WriteLine("The configuration is valid.");
            return ExitCodes.Success;
        });
        return command;
    }
}
