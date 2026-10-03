using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
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
    /// <summary>The console's commands, each written after <paramref name="prefix"/>; <c>sof chat</c>'s start with a slash.</summary>
    internal static string Help(string prefix = "") => $"""
        Commands: {prefix}status | {prefix}approve <n> | {prefix}deny <n> | {prefix}change <n> <json> | {prefix}answer <n> <text> | {prefix}tell <agent> <text>
                  | {prefix}mode <ask|auto|readOnly> | {prefix}pause [agent] | {prefix}resume [agent] | {prefix}cancel [agent] | {prefix}checkpoint | {prefix}board
                  | {prefix}task add|edit|priority|assign|cancel|show ... ({prefix}task --help) | {prefix}memory | {prefix}memory approve <n> [reason]
                  | {prefix}memory reject <n> <reason>
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
    /// stored: its agent and trigger are the stored ones. Everything is closed afterwards, the working copies too unless
    /// <paramref name="leaveWorkingCopies"/> keeps them for a run that goes on. <paramref name="trigger"/> is how a new run's work
    /// arrives: a run of a conversation, new or stored, runs with the conversation <c>sof chat</c> keeps
    /// (<see cref="ChatCommand.Conversing"/>), so a chat message's run resumes as it ran. <paramref name="owner"/> is the owner's
    /// console when it outlives the run, as in <c>sof chat</c>; there is a new one otherwise.
    /// </summary>
    internal static async Task<int> ExecuteAsync(
        ParseResult parse, ConfigurationCommandOptions shared, SofEnvironment host, string runId, string? agentName, bool existing, bool leaveWorkingCopies,
        Func<Session, CancellationToken, Task<int>> body, CancellationToken ct, Trigger trigger = Trigger.Request, OwnerQueue? owner = null)
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
            trigger = stored.Started.Trigger;
        }

        if (ConfigurationCommandOptions.Agent(agentName, options, host) is not { } name)
        {
            return ExitCodes.Usage;
        }

        if (trigger == Trigger.Conversation)
        {
            if (ChatCommand.Refusal(configuration, name) is { } refusal)
            {
                host.Error.WriteLine($"error: {refusal}");
                return ExitCodes.Invalid;
            }

            options = ChatCommand.Conversing(configuration, name);
        }

        // INV-06: the API key and the tool servers' secrets are read through the secrets the runner removes from what tools return.
        var secrets = new KnownSecrets(new EnvironmentSecrets(host.Variables));
        var (providers, claude) = Providers(options, host, secrets);
        using var _claude = claude;

        if (options.Models[options.Agents[name].Model].Provider is var missing && !providers.ContainsKey(missing))
        {
            host.Error.WriteLine($"error: provider \"{missing}\" is not available in this build of sof.");
            return ExitCodes.Usage;
        }

        var queue = owner ?? new OwnerQueue(TextWriter.Synchronized(host.Out));
        var output = queue.Output;
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
        catch (OperationCanceledException) when (starting && ct.IsCancellationRequested)
        {
            // Ctrl+C while the tool servers, workspace and storage open: nothing ran, and what was opened is released by the usings above.
            host.Error.WriteLine("cancelled before the run started.");
            return ExitCodes.NotCompleted;
        }
        catch (Exception exception) when (starting && exception is ConfigurationException or WorkspaceException or InvalidOperationException or InvalidDataException or KeyNotFoundException or IOException or HttpRequestException)
        {
            // Only what stops the run from starting; a failure during the run is not a configuration error.
            host.Error.WriteLine($"error: {exception.Message}");
            return ExitCodes.Invalid;
        }
    }

    /// <summary>
    /// The model providers a run uses: the host's, and the Claude provider for <c>providers.claude</c> unless the host has one. The
    /// Claude provider is returned too, for the caller to dispose of; it reads its key from <paramref name="secrets"/> at its first call.
    /// </summary>
    internal static (Dictionary<string, IModelProvider> Providers, ClaudeProvider? Claude) Providers(OfficinaOptions options, SofEnvironment host, ISecretSource secrets)
    {
        var providers = new Dictionary<string, IModelProvider>(host.Providers);
        var claude = !providers.ContainsKey(ProviderOptions.ClaudeName) && options.Providers.TryGetValue(ProviderOptions.ClaudeName, out var claudeOptions)
            ? new ClaudeProvider(claudeOptions, secrets) : null;
        if (claude is not null)
        {
            providers[ProviderOptions.ClaudeName] = claude;
        }

        return (providers, claude);
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
        var (name, output) = (session.Agent, session.Output);
        var status = new StatusView(output, session.Workspace is null ? null : () => session.Workspace.Queue);
        var result = await WatchAsync(session, status, start, stop =>
        {
            // A read from the console blocks its thread and cannot be cancelled, so the command loop runs on a thread of its own
            // and is left to end with the process.
            _ = Task.Run(() => CommandAsync(session, status, host.In, stop), CancellationToken.None);
            return Task.CompletedTask;
        });

        output.WriteLine();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: {result.Outcome}{Handoff(result)}, cost ${result.Statistics.Cost:0.00}"));
        output.WriteLine(result.Output);
        if (await RunReport.BuildAsync(session.Storage, null, session.RunId, CancellationToken.None) is { } report)
        {
            output.WriteLine();
            output.Write(report.ToText()); // RUN-11
        }

        return result.Outcome == AgentOutcome.Completed ? ExitCodes.Success : ExitCodes.NotCompleted;
    }

    /// <summary>
    /// Runs the work <paramref name="start"/> starts, and shows its events on <paramref name="status"/> as they happen (UX-01) while
    /// <paramref name="commands"/> takes the owner's commands. Their token is cancelled when the run ends, and they are awaited.
    /// </summary>
    internal static async Task<AgentResult> WatchAsync(Session session, StatusView status, Func<Task<AgentResult>> start, Func<CancellationToken, Task> commands)
    {
        using var stop = new CancellationTokenSource();
        session.Output.WriteLine($"run {session.RunId}");
        var watching = WatchAsync(session.Runner, session.RunId, status, stop.Token);
        var commanding = commands(stop.Token);
        try
        {
            return await start();
        }
        finally
        {
            await stop.CancelAsync();
            await watching;
            await commanding;
        }
    }

    /// <summary>Why a run was handed off, for the line that says how it ended.</summary>
    internal static string Handoff(AgentResult result) => result.Handoff is { } handoff ? $" ({handoff.Reason}: {handoff.Detail})" : "";

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
    private static async Task CommandAsync(Session session, StatusView status, TextReader input, CancellationToken ct)
    {
        try
        {
            // A line read is the run's even if the run ends meanwhile: a console that is shared, as in a chat session, keeps
            // a line taken after the run stopped reading (ChatSession.SessionReader).
            while (await input.ReadLineAsync(ct) is { } line)
            {
                await CarryOutAsync(line, session, status, Help(), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Carries out one of the owner's commands on the run; <paramref name="help"/> is shown for one it does not know.</summary>
    internal static async Task CarryOutAsync(string line, Session session, StatusView status, string help, CancellationToken ct)
    {
        var (runner, output) = (session.Runner, session.Output);
        try
        {
            if (line.Trim() == "checkpoint")
            {
                output.WriteLine(await CheckpointAsync(runner, session.RunId, ct));
            }
            else if (CommandLineParser.SplitCommandLine(line).ToList() is [_, ..] words && words[0] is "board" or "task")
            {
                // TASK-08: the live run's board, which its team sees changed at its next look.
                output.WriteLine(await TaskCommand.CarryOutAsync(
                    words, new OwnerBoard(() => runner.Board(null, session.RunId), runner.Options, session.Agent, Live: true, session.Queue.Prefix), ct));
            }
            else if (line.Trim().Split(' ', 4, StringSplitOptions.RemoveEmptyEntries) is ["memory", .. var memory])
            {
                output.Write(await MemoryAsync(runner, memory, ct));
            }
            else if (Apply(line.Trim(), session, status, help) is { } problem)
            {
                output.WriteLine(problem);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A command that fails says so, and the owner can go on typing.
            output.WriteLine($"error: {exception.Message}");
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
    private static string? Apply(string line, Session session, StatusView status, string help)
    {
        var (runner, agent, runId, queue) = (session.Runner, session.Agent, session.RunId, session.Queue);
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
                    return help;
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
