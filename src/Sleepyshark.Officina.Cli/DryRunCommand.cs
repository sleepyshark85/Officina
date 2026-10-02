using System.CommandLine;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Sandbox;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// <c>sof config dry-run [--agent &lt;name&gt;] [--input &lt;text&gt;] --reply &lt;text&gt;…</c>: validates the
/// configuration, shows it, and runs an agent against a scripted model, with no real model calls (CFG-12). The workspace's
/// tools work on files in memory, and commands, the sandbox's and the command checks', are not run: each answers that it was
/// not, with exit code 0. The command rules still decide which may run.
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

            if (ConfigurationCommandOptions.Agent(parse.GetValue(agent), configuration.Options, host) is not { } name)
            {
                return ExitCodes.Usage;
            }

            var options = configuration.Options;
            var (tools, gates, checks) = (new Dictionary<string, ITool>(), new Dictionary<string, IGate>(), new Dictionary<string, ICheck>());
            var workspace = options.Capabilities.Workspace.Enabled ? new InMemoryWorkspace() : null;
            foreach (var (id, tool) in workspace is null ? new Dictionary<string, ITool>() : new WorkspaceTools(call => workspace.OpenWorkingCopyAsync(call.WorkingCopy, call.Agent, ct)).Tools)
            {
                tools[id] = tool;
            }

            using var folder = options.Capabilities.Sandbox.Enabled ? new DryRunFolder() : null;
            if (folder is not null)
            {
                var sandbox = new FakeSandbox { Answer = _ => ("(dry run: the command was not run)", 0) };
                foreach (var (id, tool) in new SandboxTools(sandbox, options.Capabilities.Sandbox, options.Capabilities.Workspace, "", folder.Path).Tools)
                {
                    tools[id] = tool;
                }

                gates[CommandRules.Id] = new CommandRules(options.Capabilities.Sandbox);
                foreach (var (checkName, check) in options.Checks.Where(check => check.Value.Command is not null))
                {
                    checks[check.Id(checkName)] = new CommandCheck(
                        sandbox, options.Capabilities.Sandbox, options.Capabilities.Workspace, InstructionPlaceholders.FillCommand(check.Command!, options.Project), check.Timeout, TimeProvider.System);
                }
            }

            TestKit kit;
            try
            {
                kit = new TestKit(options, tools, gates, checks: checks, workspace: workspace);
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

    /// <summary>An empty folder that stands for the working copy the sandbox's tools are given, removed after the dry run.</summary>
    private sealed class DryRunFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("sof-dry-run-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
