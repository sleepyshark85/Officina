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

            // What sof run refuses as it starts: extensions sof does not register, write tools with no gate, and what the models' providers lack. No model is
            // called and nothing is opened.
            var (providers, claude) = RunCommand.Providers(configuration.Options, host, new EnvironmentSecrets(host.Variables));
            using (claude)
            {
                IReadOnlyList<ConfigurationError> errors =
                    [
                        .. WorkspaceHost.RegistrationErrors(configuration.Options),
                        .. AgentRunner.GateErrors(configuration.Options, WorkspaceHost.RegisteredTools(configuration.Options)),
                        .. Unavailable(configuration.Options, providers),
                        .. AgentRunner.ProviderErrors(configuration.Options, providers, new Dictionary<string, IHistoryShortener>()),
                    ];
                if (errors.Count > 0)
                {
                    return ConfigurationCommandOptions.ReportErrors(errors, host);
                }
            }

            // What sof chat refuses as it starts: not an error, as the other commands run with it.
            foreach (var refusal in ChatCommand.Refusals(configuration))
            {
                host.Out.WriteLine($"note: sof chat refuses this: {refusal}");
            }

            foreach (var warning in ChatCommand.WorkspaceWarnings(configuration.Options))
            {
                host.Out.WriteLine($"note: in sof chat, {warning}");
            }

            host.Out.WriteLine("The configuration is valid.");
            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>An agent's model whose provider this build of sof has no implementation of: sof run refuses to run the agent.</summary>
    private static IEnumerable<ConfigurationError> Unavailable(OfficinaOptions options, IReadOnlyDictionary<string, IModelProvider> providers) =>
        options.Agents.Values.Select(agent => agent.Model).Distinct()
            .Where(model => !providers.ContainsKey(options.Models[model].Provider))
            .Select(model => new ConfigurationError(
                ValidationPhase.Provider, $"models.{model}.provider", $"provider \"{options.Models[model].Provider}\" is not available in this build of sof.",
                $"Use one it has: {string.Join(", ", providers.Keys.Order(StringComparer.Ordinal))}."));
}
