using System.CommandLine;
using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Reports;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Mcp;
using Sleepyshark.Officina.Providers.Claude;
using Sleepyshark.Officina.Storage.Sqlite;
using Sleepyshark.Officina.Workspace;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// <c>sof run [--agent &lt;name&gt;] --input &lt;text&gt;</c>: runs an agent with the owner at the console. The run's
/// events are shown as they happen (UX-01), and the owner answers what waits for them, messages agents, changes the
/// permission mode, and pauses or resumes the run or one agent, and cancels an agent, by typing commands (HITL, RUN-06). In a team,
/// agents are named by their id, such as developer[2]. Ctrl+C cancels the run. The
/// run is stored in <c>.sof/sof.db</c> in the project directory (STO-01).
/// </summary>
internal static class RunCommand
{
    private const string Help = """
        Commands: status | approve <n> | deny <n> | change <n> <json> | answer <n> <text> | tell <agent> <text>
                  | mode <ask|auto|readOnly> | pause [agent] | resume [agent] | cancel [agent] | checkpoint | board
                  | memory | memory approve <n> [reason] | memory reject <n> <reason>
        """;

    public static Command Create(ConfigurationCommandOptions shared, SofEnvironment host)
    {
        var agent = new Option<string>("--agent") { Description = "The agent to run (default: the only agent)." };
        var input = new Option<string>("--input") { Description = "The work it is given.", Required = true };
        var command = new Command("run", "Run an agent, with you at the console to answer it.") { agent, input };
        shared.AddTo(command);
        command.SetAction((parse, ct) =>
        {
            var work = new Work("", parse.GetValue(input)!);
            return ExecuteAsync(parse, shared, host, work.RunId, parse.GetValue(agent), existing: false, leaveWorkingCopies: false, async (session, token) =>
            {
                var started = work with { Agent = session.Agent };
                return await RunAsync(session, () => session.Runner.RunAsync(started, token), host, token);
            }, ct);
        });
        return command;
    }

    /// <summary>What a command that works on a run needs, once the configuration is loaded and everything it uses is open.</summary>
    internal sealed record Session(AgentRunner Runner, string Agent, string RunId, OwnerQueue Queue, TextWriter Output, WorkspaceHost? Workspace, IStorage Storage);

    /// <summary>
    /// Loads the configuration, opens what a run uses (the tool servers, the workspace, the storage in <c>.sof/sof.db</c>, the
    /// runner), and does what <paramref name="body"/> says with it. An <paramref name="existing"/> run is one that is already
    /// stored: its agent is the stored one. Everything is closed afterwards, the working copies too unless
    /// <paramref name="leaveWorkingCopies"/> keeps them for a run that goes on.
    /// </summary>
    internal static async Task<int> ExecuteAsync(
        ParseResult parse, ConfigurationCommandOptions shared, SofEnvironment host, string runId, string? agentName, bool existing, bool leaveWorkingCopies,
        Func<Session, CancellationToken, Task<int>> body, CancellationToken ct)
    {
        var configuration = shared.Load(parse, host);
        if (ConfigurationCommandOptions.ReportErrors(configuration, host) is var code && code != ExitCodes.Success)
        {
            return code;
        }

        var options = configuration.Options;
        var directory = shared.Directory(parse, host);
        var state = Directory.CreateDirectory(Path.Combine(directory, WorkspaceOptions.StateFolder)).FullName;

        // RUN-04: a run is held by one process at a time, so a run that is still going is not resumed or rolled back from another.
        FileStream? held;
        try
        {
            held = new FileStream(Path.Combine(state, $"{runId}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            host.Error.WriteLine($"error: run {runId} is held by another process, so it is still running.");
            return ExitCodes.Invalid;
        }

        await using var _ = held;
        if (existing)
        {
            // RUN-04: a run that is stored has the agent it was started with.
            if (await StoredRunAsync(state, runId, host, ct) is not { } stored)
            {
                return ExitCodes.Invalid;
            }

            agentName = stored.Started.Agent;
        }

        if (ConfigurationCommandOptions.Agent(agentName, options, host) is not { } name)
        {
            return ExitCodes.Usage;
        }

        // INV-06: the API key and the tool servers' secrets are read through the secrets the runner removes from what tools return.
        var secrets = new KnownSecrets(new EnvironmentSecrets(host.Variables));
        var providers = new Dictionary<string, IModelProvider>(host.Providers);
        using var claude = !providers.ContainsKey(ProviderOptions.ClaudeName) && options.Providers.TryGetValue(ProviderOptions.ClaudeName, out var claudeOptions)
            ? new ClaudeProvider(claudeOptions, secrets) : null;
        if (claude is not null)
        {
            providers[ProviderOptions.ClaudeName] = claude;
        }

        if (options.Models[options.Agents[name].Model].Provider is var missing && !providers.ContainsKey(missing))
        {
            host.Error.WriteLine($"error: provider \"{missing}\" is not available in this build of sof.");
            return ExitCodes.Usage;
        }

        var output = TextWriter.Synchronized(host.Out);
        var queue = new OwnerQueue(output);
        var starting = true;
        int? exitCode = null;
        try
        {
            await using var servers = options.ToolServers.Count > 0 ? await ToolServers.ConnectAsync(options, secrets, ct) : null;
            var tools = new Dictionary<string, ITool>(servers?.Tools ?? new Dictionary<string, ITool>());
            IStorage storage = await SqliteStorage.OpenAsync(Path.Combine(state, "sof.db"), ct);
            await using var workspace = options.Capabilities.Workspace.Enabled
                ? await WorkspaceHost.OpenAsync(
                    options, directory, runId, options.Capabilities.Sandbox.Enabled ? host.Sandbox ?? WorkspaceHost.MachineSandbox() : null, host.Time,
                    async task => task.Length > RunIdLength && await storage.Runs.ReadAsync(null, task[..RunIdLength], ct) is { Status: RunStatus.Running }, leaveWorkingCopies, host.Error, ct)
                : null;
            foreach (var (id, tool) in workspace?.Tools ?? new Dictionary<string, ITool>())
            {
                tools[id] = tool;
            }

            var runner = new AgentRunner(
                options, providers, storage, tools, new Dictionary<string, IGate>(workspace?.Gates ?? new Dictionary<string, IGate>()), new Dictionary<string, ICheck>(workspace?.Checks ?? new Dictionary<string, ICheck>()),
                new Dictionary<string, IKnowledgeSource>(), queue, secrets, host.Time, workspace: workspace);
            starting = false;
            exitCode = await body(new Session(runner, name, runId, queue, output, workspace, storage), ct);
            return exitCode.Value;
        }
        catch (AggregateException exception) when (exitCode is { } ran)
        {
            // Only the workspace's cleanup after the run throws this; the run's own exit code stands.
            host.Error.WriteLine($"warning: cleanup after the run failed, so some of it is left behind: {string.Join("; ", exception.InnerExceptions.Select(inner => inner.Message))}");
            return ran;
        }
        catch (Exception exception) when (starting && exception is ConfigurationException or WorkspaceException or InvalidOperationException or InvalidDataException or KeyNotFoundException or IOException or HttpRequestException)
        {
            // Only what stops the run from starting; a failure during the run is not a configuration error.
            host.Error.WriteLine($"error: {exception.Message}");
            return ExitCodes.Invalid;
        }
    }

    /// <summary>
    /// A stored run, read from the project's database; null, with the error printed, when there is none or the database is in another format.
    /// </summary>
    internal static async Task<StoredRun?> StoredRunAsync(string state, string runId, SofEnvironment host, CancellationToken ct)
    {
        var database = Path.Combine(state, "sof.db");
        try
        {
            if (File.Exists(database) && await ((IStorage)await SqliteStorage.OpenAsync(database, ct)).Runs.ReadAsync(null, runId, ct) is { } stored)
            {
                return stored;
            }

            host.Error.WriteLine($"error: there is no run {runId}.");
        }
        catch (InvalidDataException exception)
        {
            host.Error.WriteLine($"error: {exception.Message}");
        }

        return null;
    }

    /// <summary>A run's id is a GUID, and the first part of its working copies' names (<see cref="WorkspaceHost"/>).</summary>
    private const int RunIdLength = 36;

    /// <summary>Shows a run's events and takes the owner's commands while it runs, then prints how it ended.</summary>
    internal static async Task<int> RunAsync(Session session, Func<Task<AgentResult>> start, SofEnvironment host, CancellationToken ct)
    {
        var (runner, name, queue, output) = (session.Runner, session.Agent, session.Queue, session.Output);
        var status = new StatusView(output, session.Workspace is null ? null : () => session.Workspace.Queue);
        using var stop = new CancellationTokenSource();
        output.WriteLine($"run {session.RunId}");
        var watching = WatchAsync(runner, session.RunId, status, stop.Token);
        // A read from the console cannot be cancelled, so the command loop is left to end with the process.
        _ = CommandAsync(runner, name, session.RunId, queue, status, host.In, output, stop.Token);
        var result = await start();
        await stop.CancelAsync();
        await watching;

        output.WriteLine();
        output.WriteLine($"{name}: {result.Outcome}{(result.Handoff is { } handoff ? $" ({handoff.Reason}: {handoff.Detail})" : "")}, cost ${result.Statistics.Cost:0.00}");
        output.WriteLine(result.Output);
        if (await RunReport.BuildAsync(session.Storage, null, session.RunId, CancellationToken.None) is { } report)
        {
            output.WriteLine();
            output.Write(report.ToText()); // RUN-11
        }

        return result.Outcome == AgentOutcome.Completed ? ExitCodes.Success : ExitCodes.NotCompleted;
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
        AgentRunner runner, string agent, string runId, OwnerQueue queue, StatusView status, TextReader input, TextWriter output, CancellationToken ct)
    {
        try
        {
            while (await input.ReadLineAsync(ct) is { } line && !ct.IsCancellationRequested)
            {
                if (line.Trim() == "checkpoint")
                {
                    output.WriteLine(await CheckpointAsync(runner, runId, ct));
                }
                else if (line.Trim() == "board")
                {
                    output.Write(await BoardAsync(runner, runId, ct));
                }
                else if (line.Trim().Split(' ', 4, StringSplitOptions.RemoveEmptyEntries) is ["memory", .. var memory])
                {
                    output.Write(await MemoryAsync(runner, memory, ct));
                }
                else if (Apply(line.Trim(), runner, agent, runId, queue, status) is { } problem)
                {
                    output.WriteLine(problem);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>RUN-03: the owner takes a checkpoint of the run now.</summary>
    private static async Task<string> CheckpointAsync(AgentRunner runner, string runId, CancellationToken ct)
    {
        try
        {
            return await runner.CheckpointAsync(runId, ct: ct) is { } taken ? $"checkpoint {taken.Number} taken." : "error: checkpoints are off.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return $"error: {exception.Message}";
        }
    }

    /// <summary>TASK-08: the run's task board as it is now, one task a line, the highest priority first.</summary>
    private static async Task<string> BoardAsync(AgentRunner runner, string runId, CancellationToken ct)
    {
        if (!runner.Options.Capabilities.TaskBoard.Enabled)
        {
            return "error: the task board is off.\n";
        }

        var tasks = await runner.Board(null, runId).ReadAsync(ct);
        return tasks.Count == 0 ? "the board is empty.\n" : string.Concat(tasks.Select(task =>
            $"{task.Id} {task.Title}: {task.State}{(task.Assignee is { } assignee ? $", with {assignee}" : "")}{(task.Role is { } role ? $", role {role}" : "")}"
            + $"{(task.DependsOn.Count > 0 ? $", depends on {string.Join(", ", task.DependsOn)}" : "")}, priority {task.Priority}, ${task.Spent:0.00} of ${task.Budget:0.00}\n"));
    }

    /// <summary>
    /// MEM-03, MEM-05: the owner lists the proposed changes to project memory, and approves or rejects one, the condensing that only
    /// the owner approves included.
    /// </summary>
    private static async Task<string> MemoryAsync(AgentRunner runner, string[] words, CancellationToken ct)
    {
        if (!runner.Options.Capabilities.ProjectMemory.Enabled)
        {
            return "error: project memory is off.\n";
        }

        var memory = runner.Memory(Caller.Anonymous);
        if (words is [])
        {
            var pending = (await memory.ReadAsync(ct)).Pending;
            return pending.Count == 0 ? "no changes to project memory wait for you.\n" : string.Concat(pending.Select(proposal =>
                $"#{proposal.Id} {proposal.Content.Kind.ToString().ToLowerInvariant()} {proposal.Content.Subject}: {proposal.Content.Text} (by {proposal.By})"
                + (proposal.Content.Replaces is { Count: > 0 } replaces ? $", replaces {string.Join(", ", replaces.Select(id => $"#{id}"))}" : "") + "\n"));
        }

        if (words is not [var action and ("approve" or "reject"), var number, ..] || !long.TryParse(number.TrimStart('#'), out var id)
            || (action == "reject" && words.Length < 3))
        {
            return "error: memory approve <n> [reason] | memory reject <n> <reason>\n";
        }

        var (_, text) = action == "approve"
            ? await memory.ApproveAsync(id, words.Length > 2 ? words[2] : null, ct)
            : await memory.RejectAsync(id, words[2], ct);
        return text + "\n";
    }

    /// <summary>Carries out one command; returns what was wrong with it, if anything.</summary>
    private static string? Apply(string line, AgentRunner runner, string agent, string runId, OwnerQueue queue, StatusView status)
    {
        var words = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var target = words.Length > 1 ? words[1] : null;
        var team = runner.Options.Agents[agent].Pattern.Type == PatternOptions.Team;
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
                case "tell" when words.Length == 3 && team:
                    runner.Send(runId, words[1], Sender.Owner, words[2]); // HITL-03: an agent of the run's team, by its id
                    return null;
                case "tell" when words.Length == 3:
                    runner.Send(words[1], Sender.Owner, words[2]); // HITL-03
                    return null;
                case "mode" when words.Length == 2 && Enum.TryParse<PermissionMode>(words[1], ignoreCase: true, out var mode):
                    runner.PermissionMode = mode;
                    return null;
                case "pause" when target is null:
                    runner.PauseRun(runId); // RUN-06: the whole run
                    return null;
                case "pause" when team:
                    runner.Pause(runId, target); // an agent of the run's team, by its id
                    return null;
                case "pause":
                    runner.Pause(target);
                    return null;
                case "resume" when target is null:
                    runner.ResumeRun(runId);
                    return null;
                case "resume" when team:
                    runner.Resume(runId, target);
                    return null;
                case "resume":
                    runner.Resume(target);
                    return null;
                case "cancel" when target is not null && team:
                    runner.Cancel(runId, target);
                    return null;
                case "cancel":
                    runner.Cancel(target ?? agent);
                    return null;
                default:
                    return Help;
            }
        }
        catch (Exception exception) when (exception is ConfigurationException or JsonException or ArgumentException)
        {
            return $"error: {exception.Message}";
        }
    }

    private static string? Answer(string[] words, OwnerQueue queue, string command, HumanAnswer answer) =>
        words.Length > 1 && int.TryParse(words[1].TrimStart('#'), out var number)
            ? queue.Answer(number, command, answer)
            : "error: nothing waits for you with that number.";
}
