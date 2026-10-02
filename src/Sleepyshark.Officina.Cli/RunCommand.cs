using System.CommandLine;
using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Storage.Sqlite;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// <c>sof run [--agent &lt;name&gt;] --input &lt;text&gt;</c>: runs an agent with the owner at the console. The run's
/// events are shown as they happen (UX-01), and the owner answers what waits for them, messages agents, changes the
/// permission mode, and pauses, resumes or cancels agents by typing commands (HITL, RUN-06). Ctrl+C cancels the run. The
/// run is stored in <c>.sof/sof.db</c> in the project directory (STO-01).
/// </summary>
internal static class RunCommand
{
    private const string Help = """
        Commands: status | approve <n> | deny <n> | change <n> <json> | answer <n> <text> | tell <agent> <text>
                  | mode <ask|auto|readOnly> | pause [agent] | resume [agent] | cancel [agent]
        """;

    public static Command Create(ConfigurationCommandOptions shared, SofEnvironment host)
    {
        var agent = new Option<string>("--agent") { Description = "The agent to run (default: the only agent)." };
        var input = new Option<string>("--input") { Description = "The work it is given.", Required = true };
        var command = new Command("run", "Run an agent, with you at the console to answer it.") { agent, input };
        shared.AddTo(command);
        command.SetAction(async (parse, ct) =>
        {
            var configuration = shared.Load(parse, host);
            if (ConfigurationCommandOptions.ReportErrors(configuration, host) is var code && code != ExitCodes.Success)
            {
                return code;
            }

            if (ConfigurationCommandOptions.Agent(parse.GetValue(agent), configuration.Options, host) is not { } name)
            {
                return ExitCodes.Usage;
            }

            var options = configuration.Options;
            if (options.Models[options.Agents[name].Model].Provider is var missing && !host.Providers.ContainsKey(missing))
            {
                host.Error.WriteLine($"error: provider \"{missing}\" is not available in this build of sof.");
                return ExitCodes.Usage;
            }

            var output = TextWriter.Synchronized(host.Out);
            var queue = new OwnerQueue(output);
            var status = new StatusView(output);
            var state = Directory.CreateDirectory(Path.Combine(shared.Directory(parse, host), WorkspaceOptions.StateFolder)).FullName;
            var storage = await SqliteStorage.OpenAsync(Path.Combine(state, "sof.db"), ct);
            AgentRunner runner;
            try
            {
                runner = new AgentRunner(
                    options, host.Providers, storage, new Dictionary<string, ITool>(), new Dictionary<string, IGate>(), new Dictionary<string, ICheck>(),
                    new Dictionary<string, IKnowledgeSource>(), queue, new EnvironmentSecrets(host.Variables), host.Time);
            }
            catch (ConfigurationException exception)
            {
                host.Error.WriteLine($"error: {exception.Message}");
                return ExitCodes.Invalid;
            }

            var work = new Work(name, parse.GetValue(input)!);
            using var stop = new CancellationTokenSource();
            var watching = WatchAsync(runner, work.RunId, status, stop.Token);
            // A read from the console cannot be cancelled, so the command loop is left to end with the process.
            _ = CommandAsync(runner, name, queue, status, host.In, output, stop.Token);
            var result = await runner.RunAsync(work, ct);
            await stop.CancelAsync();
            await watching;

            output.WriteLine();
            output.WriteLine($"{name}: {result.Outcome}{(result.Handoff is { } handoff ? $" ({handoff.Reason}: {handoff.Detail})" : "")}, cost ${result.Statistics.Cost:0.00}");
            output.WriteLine(result.Output);
            return result.Outcome == AgentOutcome.Completed ? ExitCodes.Success : ExitCodes.NotCompleted;
        });
        return command;
    }

    /// <summary>Shows the run's events until the run ends.</summary>
    private static async Task WatchAsync(AgentRunner runner, string runId, StatusView status, CancellationToken ct)
    {
        try
        {
            await foreach (var coreEvent in runner.Events.ReadAsync(null, runId, 0, ct))
            {
                status.Apply(coreEvent);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Carries out the owner's commands, one per line, until the run ends or the input does.</summary>
    private static async Task CommandAsync(
        AgentRunner runner, string agent, OwnerQueue queue, StatusView status, TextReader input, TextWriter output, CancellationToken ct)
    {
        try
        {
            while (await input.ReadLineAsync(ct) is { } line && !ct.IsCancellationRequested)
            {
                if (Apply(line.Trim(), runner, agent, queue, status) is { } problem)
                {
                    output.WriteLine(problem);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Carries out one command; returns what was wrong with it, if anything.</summary>
    private static string? Apply(string line, AgentRunner runner, string agent, OwnerQueue queue, StatusView status)
    {
        var words = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var target = words.Length > 1 ? words[1] : agent;
        try
        {
            switch (words.FirstOrDefault())
            {
                case null:
                    return null;
                case "status":
                    status.Print(queue);
                    return null;
                case "approve":
                    return Answer(words, queue, words[0], HumanAnswer.Approve);
                case "deny":
                    return Answer(words, queue, words[0], HumanAnswer.Deny);
                case "change" when words.Length == 3:
                    return Answer(words, queue, words[0], HumanAnswer.ApproveChanged(JsonDocument.Parse(words[2]).RootElement.Clone()));
                case "answer" when words.Length == 3:
                    return Answer(words, queue, words[0], HumanAnswer.Reply(words[2]));
                case "tell" when words.Length == 3:
                    runner.Send(words[1], Sender.Owner, words[2]); // HITL-03
                    return null;
                case "mode" when words.Length == 2 && Enum.TryParse<PermissionMode>(words[1], ignoreCase: true, out var mode):
                    runner.PermissionMode = mode;
                    return null;
                case "pause":
                    runner.Pause(target);
                    return null;
                case "resume":
                    runner.Resume(target);
                    return null;
                case "cancel":
                    runner.Cancel(target);
                    return null;
                default:
                    return Help;
            }
        }
        catch (Exception exception) when (exception is ConfigurationException or JsonException)
        {
            return $"error: {exception.Message}";
        }
    }

    private static string? Answer(string[] words, OwnerQueue queue, string command, HumanAnswer answer) =>
        words.Length > 1 && int.TryParse(words[1].TrimStart('#'), out var number)
            ? queue.Answer(number, command, answer)
            : "error: nothing waits for you with that number.";
}
