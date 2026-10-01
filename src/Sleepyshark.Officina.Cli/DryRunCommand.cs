using System.CommandLine;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// <c>sof config dry-run [--agent &lt;name&gt;] [--input &lt;text&gt;] --reply &lt;text&gt;…</c>: validates the
/// configuration, shows it, and runs an agent against a scripted model, with no real model calls (CFG-12).
/// </summary>
internal static class DryRunCommand
{
    public static Command Create(ConfigurationCommandOptions shared, SofEnvironment host)
    {
        var agent = new Option<string>("--agent") { Description = "The agent to run (default: the only agent)." };
        var input = new Option<string>("--input") { Description = "The work it is given." };
        var replies = new Option<string[]>("--reply") { Description = "A reply of the scripted model. Repeat it for each model call, in order.", Required = true };
        var command = new Command("dry-run", "Validate and show the configuration, then run an agent against a scripted model.") { agent, input, replies };
        shared.AddTo(command);
        command.SetAction(async (parse, ct) =>
        {
            var configuration = shared.Load(parse, host);
            ShowCommand.Print(configuration, host.Out, origin: false);
            if (ConfigurationCommandOptions.ReportErrors(configuration, host) is var code && code != ExitCodes.Success)
            {
                return code;
            }

            var agents = configuration.Options.Agents;
            if ((parse.GetValue(agent) ?? (agents.Count == 1 ? agents.Keys.Single() : null)) is not { } name || !agents.ContainsKey(name))
            {
                host.Error.WriteLine($"error: name the agent to run with --agent, one of: {string.Join(", ", agents.Keys.Order(StringComparer.Ordinal))}.");
                return ExitCodes.Usage;
            }

            TestKit kit;
            try
            {
                kit = new TestKit(configuration.Options);
            }
            catch (ConfigurationException exception)
            {
                // Errors only the application's tools reveal, such as a tool that is not registered (TOOL-02).
                host.Error.WriteLine($"error: {exception.Message}");
                return ExitCodes.Invalid;
            }

            foreach (var reply in parse.GetValue(replies)!)
            {
                kit.Model.Reply(reply);
            }

            var result = await kit.RunAsync(name, parse.GetValue(input) ?? "", ct);
            host.Out.WriteLine();
            host.Out.WriteLine($"{name}: {result.Outcome}{(result.Handoff is { } handoff ? $" ({handoff.Reason})" : "")}");
            host.Out.WriteLine(result.Output);
            return ExitCodes.Success;
        });
        return command;
    }
}
