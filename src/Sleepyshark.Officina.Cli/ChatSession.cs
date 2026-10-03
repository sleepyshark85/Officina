using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Reports;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// One chat session (<c>sof</c> or <c>sof chat</c>). The console is read on a thread of its own, as a read blocks its thread, and
/// the session takes each line from <see cref="lines"/>. Each message is a run, opened and closed as <c>sof run</c>'s is, so
/// between replies the session holds no run, lock or store, and every <c>sof</c> command can be typed after a <c>/</c>. While a
/// reply runs, a command is carried out at once and a message waits until the reply ends. Ctrl+C cancels the reply or the
/// command; a second Ctrl+C, <c>/quit</c> or the end of the input ends the session, the end of the input once the messages it
/// sent have their replies.
/// </summary>
[SuppressMessage("Reliability", "CA1001", Justification = "The session's token source has no timer, so it holds nothing to dispose, and a signal may still cancel it as the process ends.")]
internal sealed class ChatSession
{
    /// <summary>The commands that act on the run of the reply that runs (<see cref="RunCommand.CarryOutAsync"/>).</summary>
    private static readonly HashSet<string> RunCommands = ["approve", "deny", "change", "answer", "tell", "pause", "resume", "cancel", "checkpoint", "board", "task", "memory"];

    /// <summary>The console's commands, whose names start a line shaped like a command typed without its <c>/</c> (<see cref="HeldBack"/>).</summary>
    private static readonly HashSet<string> CommandWords =
        ["approve", "deny", "answer", "change", "status", "tell", "mode", "pause", "resume", "cancel", "board", "task", "memory", "report", "help", "quit", "drop"];

    /// <summary>The <c>sof</c> commands that take the console or the workspace, so they work only between replies.</summary>
    private static readonly HashSet<string> Between = ["run", "rollback"];

    /// <summary>How soon after a Ctrl+C a console read that ends is taken for the Ctrl+C rather than the end of the input.</summary>
    private static readonly TimeSpan InterruptedRead = TimeSpan.FromSeconds(1);

    private static readonly string Help = $"""
        {RunCommand.Help("/")}
                  | /new | /drop | /report [run] | /resume <run> | /rollback <run> [--to <n>] | /run --input <text>
                  | /config validate | /config show [--origin] | /config dry-run ... | /help [command] | /quit (or Ctrl+D)
        A line that does not start with / is a message. While a reply runs, a message waits until it ends (/status lists the
        waiting messages, /drop drops them), and /tell reaches an agent at once. A line shaped like a command without its /,
        such as "approve 1", is held back; type it again to send it as a message. /approve to /memory act on the reply that runs; between
        replies, /board and /task act on the last message's run. /resume <run>, /rollback and /run work only between replies;
        type them again when the reply ends.
        """;

    private readonly ParseResult parse;
    private readonly ConfigurationCommandOptions shared;
    private readonly SofEnvironment host;
    private readonly string? named;
    private readonly Channel<string> lines = Channel.CreateUnbounded<string>(new() { SingleWriter = true });
    private readonly Queue<string> queued = new();

    /// <summary>Lines a command typed in the session took as it stopped reading; the session takes them next.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> returned = new();

    /// <summary>When the last Ctrl+C arrived, by the host's clock; see <see cref="ReadConsoleAsync"/>.</summary>
    private long interrupted;
    private readonly CancellationTokenSource ending = new();
    private readonly Lock gate = new();
    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly TextReader input;
    private readonly OwnerQueue owner;
    private readonly StatusView status;

    /// <summary>Whether the next message starts a new conversation (<c>--new</c>, <c>/new</c>).</summary>
    private bool fresh;

    /// <summary>The reply, or the command between replies, that runs now, which Ctrl+C cancels.</summary>
    private CancellationTokenSource? replying;

    /// <summary>Whether the next Ctrl+C ends the session: one was pressed after the last message or command was sent.</summary>
    private bool armed;

    private bool inputEnded;
    private string agent = "";

    /// <summary>The agents the run's commands name, for telling a line shaped like a command (<see cref="HeldBack"/>).</summary>
    private HashSet<string> agentIds = [];

    /// <summary>The line last held back as shaped like a command; typed again as the next line, it is a message.</summary>
    private string? warned;
    private bool keepsHistory;
    private PermissionMode? mode;

    /// <summary>The runs of the session's messages, in order; read by the line editor's thread too, so under its lock.</summary>
    private readonly List<string> runs = [];

    private string? LastRun
    {
        get
        {
            lock (runs)
            {
                return runs.LastOrDefault();
            }
        }
    }
    private RunCommand.Session? current;

    public ChatSession(ParseResult parse, ConfigurationCommandOptions shared, SofEnvironment host, string? named, bool fresh)
    {
        (this.parse, this.shared, this.host, this.named, this.fresh) = (parse, shared, host, named, fresh);
        // At a terminal, lines are read with the line editor, and what the session prints shares the screen with it.
        output = TextWriter.Synchronized(host.Terminal?.Writer(host.Out) ?? host.Out);
        error = TextWriter.Synchronized(host.Terminal?.Writer(host.Error) ?? host.Error);
        input = host.Terminal?.Reader() ?? host.In;
        owner = new OwnerQueue(output, "/");
        status = new StatusView(output, () => current?.Workspace?.Queue, stream: true);
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var configuration = shared.Load(parse, host);
        if (ConfigurationCommandOptions.ReportErrors(configuration, host) is var code && code != ExitCodes.Success)
        {
            if (!File.Exists(Path.Combine(shared.Directory(parse, host), "sof.json")))
            {
                error.WriteLine("There is no sof.json here to chat with: run sof init to write one for the coding team, or write one as section 3 of the user guide (docs/user-guide.md) shows.");
            }

            return code;
        }

        using var signals = host.Signals?.Invoke(Signalled);
        using var cancelled = ct.Register(End);
        // A read from the console blocks its thread and cannot be cancelled, so the console is read on a thread of its own, which
        // is left to end with the process.
        _ = Task.Run(ReadConsoleAsync, CancellationToken.None);
        using var done = new CancellationTokenSource();
        var chatting = ChatAsync(configuration);
        var forced = ForcedAsync(SofCommandLine.TerminationTimeout(configuration.Options), done.Token);
        var first = await Task.WhenAny(chatting, forced);
        await done.CancelAsync();
        if (first == chatting)
        {
            return await chatting;
        }

        // As the command line does for sof run: what has not stopped this long after the session was ended is left to end with the process.
        error.WriteLine("error: the session did not end in time, so it is left to end with the process.");
        return ExitCodes.NotCompleted;
    }

    /// <summary>Picks the agent, then takes the owner's lines until the session ends: each command is carried out and each message sent.</summary>
    private async Task<int> ChatAsync(SofConfiguration configuration)
    {
        try
        {
            var options = configuration.Options;
            if ((named is null && options.Agents.Count > 1 ? await PickAsync(options) : ConfigurationCommandOptions.Agent(named, options, host)) is not { } name)
            {
                return ExitCodes.Usage;
            }

            if (ChatCommand.Refusal(configuration, name) is { } refusal)
            {
                error.WriteLine($"error: {refusal}");
                return ExitCodes.Invalid;
            }

            agent = name;
            agentIds = [.. Agents(options, name)];
            if (input is TerminalReader terminal)
            {
                var completion = new ChatCompletion(SofCommandLine.Create(new ConfigurationCommandOptions(), host), () => Agents(options, name), RunsLatestFirst, TaskIds);
                terminal.Complete = completion.Complete;
            }

            var conversing = ChatCommand.Conversing(configuration, name);
            var keeps = ChatCommand.KeepsHistory(conversing, name);
            keepsHistory = conversing.Agents[keeps].Context.History.Strategy != HistoryStrategy.None;
            status.WriteLine($"Chatting with {name}. Each line you type is a message; /help lists the commands, and /quit or Ctrl+D ends the session.");
            if (ChatCommand.WorkspaceWarning(options, name) is { } warning)
            {
                status.WriteLine($"warning: {warning}");
            }

            if (!keepsHistory)
            {
                status.WriteLine($"note: agents.{keeps}.context.history.strategy is none, so each message starts a new conversation.");
                fresh = false;
            }
            else if (fresh)
            {
                status.WriteLine("Your first message starts a new conversation.");
            }

            while (await NextAsync() is (var line, var typed))
            {
                if (typed && HeldBack(line))
                {
                    continue;
                }

                if (line.StartsWith('/'))
                {
                    await CommandAsync(line[1..].Trim(), null);
                }
                else if (line.Trim() is { Length: > 0 } message)
                {
                    await SendAsync(message);
                }
            }

            status.WriteLine(string.Create(CultureInfo.InvariantCulture, $"The session has ended. It cost ${status.Cost:0.00}."));
            return ExitCodes.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ending.IsCancellationRequested)
        {
            // Never a failure that ends the session without a word.
            error.WriteLine($"error: the session failed: {exception.Message}");
            return ExitCodes.NotCompleted;
        }
    }

    /// <summary>With several agents and none named, the owner picks one, by its number or name; null if the input ends first.</summary>
    private async Task<string?> PickAsync(OfficinaOptions options)
    {
        var agents = options.Agents.Keys.Order(StringComparer.Ordinal).ToList();
        status.WriteLine("Which agent do you want to chat with? Type its number or name.");
        for (var index = 0; index < agents.Count; index++)
        {
            var description = options.Agents[agents[index]].Description;
            status.WriteLine($"  {index + 1}. {agents[index]}{(description is null ? "" : $": {description}")}");
        }

        while (await NextAsync() is (var line, _))
        {
            var answer = line.Trim();
            if (int.TryParse(answer, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= agents.Count)
            {
                return agents[number - 1];
            }

            if (agents.Contains(answer))
            {
                return answer;
            }

            status.WriteLine($"Type a number from 1 to {agents.Count}, or an agent's name.");
        }

        error.WriteLine("error: no agent was picked.");
        return null;
    }

    /// <summary>
    /// The next line to act on, and whether the owner typed it now: a message that waited for the last reply first, then the
    /// owner's next line; null once the session ends.
    /// </summary>
    private async Task<(string Line, bool Typed)?> NextAsync()
    {
        if (ending.IsCancellationRequested)
        {
            return null;
        }

        if (queued.TryDequeue(out var message))
        {
            return (message, false);
        }

        if (returned.TryDequeue(out message))
        {
            return (message, true);
        }

        try
        {
            var line = inputEnded ? null : await ReadAsync(lines.Reader, ending.Token);
            inputEnded = line is null;
            return line is null ? null : (line, true);
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The owner's next line, or null at the end of the input.</summary>
    private static async Task<string?> ReadAsync(ChannelReader<string> lines, CancellationToken ct)
    {
        while (await lines.WaitToReadAsync(ct))
        {
            if (lines.TryRead(out var line))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the console into <see cref="lines"/> until the input ends. On Windows, Ctrl+C ends a console read that waits with
    /// null, as the end of the input does; a null just after a Ctrl+C is taken for that, once, and the console is read again.
    /// </summary>
    private async Task ReadConsoleAsync()
    {
        try
        {
            while (true)
            {
                if (await input.ReadLineAsync(CancellationToken.None) is { } line)
                {
                    lines.Writer.TryWrite(line);
                    continue;
                }

                if (Interlocked.Exchange(ref interrupted, 0) is var at && (at == 0 || host.Time.GetElapsedTime(at) > InterruptedRead))
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            status.WriteLine($"error: the console cannot be read: {exception.Message}");
        }
        finally
        {
            lines.Writer.TryComplete();
        }
    }

    /// <summary>Sends a message: a run of the conversation, whose reply streams, with the owner's commands carried out while it runs.</summary>
    private Task SendAsync(string message) => GuardedAsync(async token =>
    {
        var work = new Work(agent, message) { Trigger = Trigger.Conversation };
        var code = await RunCommand.ExecuteAsync(
            parse, shared, host, work.RunId, agent, existing: false, leaveWorkingCopies: false, (session, ct) => ReplyAsync(session, work, ct), token,
            Trigger.Conversation, owner);
        if (code != ExitCodes.Success && !ending.IsCancellationRequested)
        {
            status.WriteLine("The message was not sent.");
        }
    });

    /// <summary>
    /// Does what a message or a command between replies does, which Ctrl+C cancels. What fails is said, and the session goes on;
    /// a message cancelled drops the messages that waited for it.
    /// </summary>
    private async Task GuardedAsync(Func<CancellationToken, Task> work)
    {
        CancellationTokenSource reply;
        lock (gate)
        {
            armed = false;
            replying = reply = CancellationTokenSource.CreateLinkedTokenSource(ending.Token);
        }

        try
        {
            await work(reply.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ending.IsCancellationRequested)
        {
            status.WriteLine($"error: {exception.Message}");
        }
        finally
        {
            lock (gate)
            {
                replying = null;
            }

            if (reply.IsCancellationRequested && !ending.IsCancellationRequested && queued.Count > 0)
            {
                status.WriteLine($"The reply was cancelled, so the {queued.Count} message(s) that waited for it are dropped.");
                queued.Clear();
            }

            reply.Dispose();
        }
    }

    private async Task<int> ReplyAsync(RunCommand.Session session, Work work, CancellationToken ct)
    {
        if (mode is { } chosen)
        {
            session.Runner.PermissionMode = chosen;
        }

        if (fresh)
        {
            await NewConversationAsync(session, ct);
            fresh = false;
        }

        lock (runs)
        {
            runs.Add(session.RunId);
        }

        current = session;
        AgentResult result;
        try
        {
            result = await RunCommand.WatchAsync(session, status, () => session.Runner.RunAsync(work, ct), stop => DuringAsync(session, stop));
        }
        finally
        {
            current = null;
            lock (gate)
            {
                replying = null; // Ctrl+C from now on is one between replies
            }
        }

        status.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"{agent}: {result.Outcome}{RunCommand.Handoff(result)}, cost ${result.Statistics.Cost:0.00}; this session ${status.Cost:0.00}"));
        if (result.Outcome != AgentOutcome.Completed && result.Output.Length > 0)
        {
            status.WriteLine(result.Output); // a completed reply has streamed already
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// <c>--new</c>, <c>/new</c>: an empty shortened turn holds the whole conversation, which is nothing yet, so the next turn starts
    /// a new one with the project memory of now (CAP-05, MEM-03). The earlier turns stay for the runs that wrote them.
    /// </summary>
    private async Task NewConversationAsync(RunCommand.Session session, CancellationToken ct)
    {
        var options = session.Runner.Options;
        var memory = options.Capabilities.ProjectMemory.Enabled ? (await session.Runner.Memory(Caller.Anonymous).ReadAsync(ct)).Revision : 0;
        var keeps = ChatCommand.KeepsHistory(options, session.Agent);
        await session.Storage.Conversations.AppendAsync(null, new ConversationTurn(keeps, null, host.Time.GetUtcNow(), [], true, memory, memory, session.RunId), ct);
    }

    /// <summary>
    /// Whether a line the owner typed is held back rather than sent: a message shaped like a command typed without its <c>/</c>,
    /// such as <c>approve</c>, <c>approve 1</c>, <c>status</c> or <c>tell lead …</c>, which as a message would start a run of its own.
    /// Typed again as the next line, it is a message. Prose that starts with a command's name, such as "Help me write…", is not held.
    /// </summary>
    private bool HeldBack(string line)
    {
        var message = line.Trim();
        var again = message == warned;
        warned = null;
        if (line.StartsWith('/') || message.Length == 0 || again || CommandShaped(message) is not { } word)
        {
            return false;
        }

        warned = message;
        status.WriteLine($"That looks like a command: type /{word}{(message.Length > word.Length ? " …" : "")}. To send it as a message instead, type it again.");
        return true;
    }

    /// <summary>
    /// The command a message is shaped like: a command's name alone, or followed by a number, by an agent (tell, pause, resume,
    /// cancel) or by a permission mode; null for anything else.
    /// </summary>
    private string? CommandShaped(string message)
    {
        var words = message.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var word = words[0].ToLowerInvariant();
        return CommandWords.Contains(word)
            && (words.Length == 1
                || words[1].TrimStart('#') is { Length: > 0 } number && number.All(char.IsAsciiDigit)
                || word is "tell" or "pause" or "resume" or "cancel" && agentIds.Contains(words[1])
                || word == "mode" && Enum.TryParse<PermissionMode>(words[1], ignoreCase: true, out _))
            ? word
            : null;
    }

    /// <summary>Takes the owner's lines while a reply runs: a command is carried out at once, and a message waits for the reply to end.</summary>
    private async Task DuringAsync(RunCommand.Session session, CancellationToken stop)
    {
        try
        {
            while (await ReadAsync(lines.Reader, stop) is { } line)
            {
                if (HeldBack(line))
                {
                    continue;
                }

                if (line.StartsWith('/'))
                {
                    await CommandAsync(line[1..].Trim(), session);
                }
                else if (line.Trim() is { Length: > 0 } message)
                {
                    queued.Enqueue(message);
                    status.WriteLine("(it is sent when this reply ends; /drop drops it, and /tell <agent> <text> reaches an agent now)");
                }
            }

            inputEnded = true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// A command typed after a <c>/</c>: the session's own, one that acts on the run of the reply that runs (<paramref name="session"/>,
    /// null between replies), or a <c>sof</c> command, parsed and run by the same command line. A command once started is carried
    /// out even if the reply ends meanwhile.
    /// </summary>
    private async Task CommandAsync(string command, RunCommand.Session? session)
    {
        var words = CommandLineParser.SplitCommandLine(command).ToList();
        var word = words.FirstOrDefault() ?? "help";
        try
        {
            switch (word)
            {
                case "quit":
                    End();
                    break;
                case "new":
                    fresh = keepsHistory;
                    status.WriteLine(keepsHistory ? "Your next message starts a new conversation." : "Each message starts a new conversation already.");
                    break;
                case "help" when words.Count == 1:
                    status.WriteLine(Help);
                    break;
                case "help" when words is [_, "task" or "board"]:
                    status.WriteLine(TaskCommand.HelpText());
                    break;
                case "help":
                    await SofAsync([.. words.Skip(1), "--help"], session);
                    break;
                case "status":
                    status.Print(owner);
                    if (queued.Count > 0)
                    {
                        status.WriteLine($"sent when the reply ends (/drop drops them):{string.Concat(queued.Select((message, index) => $"\n  {index + 1}. {message}"))}");
                    }

                    break;
                case "drop":
                    status.WriteLine(queued.Count == 0 ? "No message waits to be sent." : $"Dropped the {queued.Count} message(s) that waited for the reply to end.");
                    queued.Clear();
                    break;
                case "mode" when ModeOf(words) is { } chosen:
                    mode = chosen;
                    session?.Runner.PermissionMode = chosen;
                    break;
                case "report" when words.Count == 1:
                    await ReportAsync(session, ending.Token);
                    break;
                case "resume" when session is not null && words.Count > 1 && !Agents(session.Runner.Options, session.Agent).Contains(words[1]):
                    // A run, not an agent of the reply's run: it is sof resume, which works only between replies, as /rollback does.
                    status.WriteLine("error: /resume <run> works only between replies; type it again when the reply ends.");
                    break;
                case var _ when session is not null && RunCommands.Contains(word):
                    await RunCommand.CarryOutAsync(command, session, status, Help, ending.Token);
                    break;
                case "board" or "task" when session is null:
                    await StoredBoardAsync(words, ending.Token);
                    break;
                case var _ when session is not null && Between.Contains(word):
                    status.WriteLine($"error: /{word} works only between replies; type it again when the reply ends.");
                    break;
                case "resume" or "config" or "report" or "rollback" or "run" or "chat":
                    await SofAsync(words, session);
                    break;
                case "init":
                    // It asks at the console, which the session holds, and writes the configuration the session runs with.
                    status.WriteLine("error: run sof init in the shell, outside the session.");
                    break;
                case var _ when RunCommands.Contains(word):
                    status.WriteLine($"error: no reply is running; /{word} works while one does.");
                    break;
                default:
                    status.WriteLine(Help);
                    break;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A command that fails says so, and the owner can go on typing.
            status.WriteLine($"error: {exception.Message}");
        }
    }

    /// <summary>
    /// A <c>sof</c> command typed in the session, run by the same command line, in the session's directory and environment. Its
    /// console reads the session's lines while it runs, a <c>/</c> before a command optional, and Ctrl+C cancels it. Between replies
    /// the session holds no run, lock or store, so a command such as <c>resume</c> runs as it would from another terminal.
    /// </summary>
    private Task SofAsync(IReadOnlyList<string> args, RunCommand.Session? session)
    {
        var variables = new Dictionary<string, string>(host.Variables);
        if (shared.Environment(parse, host) is { } environment)
        {
            variables["SOF_ENVIRONMENT"] = environment;
        }

        var nested = host with
        {
            Out = output, Error = error, Terminal = null, WorkingDirectory = shared.Directory(parse, host), Variables = variables,
            In = new SessionReader(lines.Reader, returned), Signals = null, InSession = true,
        };
        return session is null
            ? GuardedAsync(token => SofCommandLine.RunAsync(args, nested, token))
            : SofCommandLine.RunAsync(args, nested, ending.Token); // only commands that read: the reply's Ctrl+C is the reply's
    }

    /// <summary>RUN-11: the report of the reply that runs, or of the last message's run.</summary>
    private async Task ReportAsync(RunCommand.Session? session, CancellationToken ct)
    {
        if ((session?.RunId ?? LastRun) is not { } run)
        {
            status.WriteLine("error: no message has been sent in this session yet; /report <run> shows any run's.");
            return;
        }

        IStorage storage = session?.Storage ?? await LocalStorage.OpenAsync(shared.Load(parse, host).Options, shared.Directory(parse, host), ct);
        status.WriteLine(await RunReport.BuildAsync(storage, null, run, ct) is { } report
            ? report.ToText().TrimEnd()
            : $"Run {run} has not been recorded yet; try again in a moment.");
    }

    /// <summary>
    /// TASK-08: <c>/board</c> and <c>/task</c> between replies act on the last message's run, straight from storage: no run, tool
    /// server or workspace is opened, as a board change touches none of the workspace. A change takes the run's lock, so a run held
    /// by another process, such as one resumed in another terminal, is refused. The run has ended, so no agent works on its board
    /// again: the change is recorded and shows in its report. A run that stopped without ending, as a rollback leaves it, is refused
    /// too, as its resume goes back to its last checkpoint's board and would undo the change.
    /// </summary>
    private async Task StoredBoardAsync(List<string> words, CancellationToken ct)
    {
        if (LastRun is not { } run)
        {
            status.WriteLine("error: no message has been sent in this session yet, so there is no board.");
            return;
        }

        var configuration = shared.Load(parse, host);
        if (ConfigurationCommandOptions.ReportErrors(configuration, host) != ExitCodes.Success)
        {
            return;
        }

        var state = Path.Combine(shared.Directory(parse, host), WorkspaceOptions.StateFolder);
        var changes = TaskCommand.Changes(words);
        FileStream? held = null;
        if (changes)
        {
            try
            {
                held = new FileStream(Path.Combine(state, $"{run}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                status.WriteLine($"error: run {run} is held by another process, so its board is changed there.");
                return;
            }
        }

        await using (held)
        {
            IStorage storage = await LocalStorage.OpenAsync(configuration.Options, shared.Directory(parse, host), ct);
            if (await storage.Runs.ReadAsync(null, run, ct) is not { } stored)
            {
                status.WriteLine($"error: run {run} was not recorded, so it has no board.");
                return;
            }

            if (changes && stored.Status == RunStatus.Running)
            {
                status.WriteLine(
                    $"error: run {run} stopped without ending, so /resume {run} goes back to its last checkpoint's board and would undo the change. Resume it, and change its board while it runs.");
                return;
            }

            var options = configuration.Options;
            var note = $"note: run {run} has ended, so no agent works on this board again; ask for the work in your next message.";
            status.WriteLine(await TaskCommand.CarryOutAsync(
                words, new OwnerBoard(() => TaskBoard.ForOwner(storage, options, null, run, host.Time), options, agent, Live: false, "/", note), ct));
        }
    }

    /// <summary>The ids of the tasks on the board of the reply that runs, or of the last message's run, for completion; none if it cannot be read.</summary>
    private List<string> TaskIds()
    {
        try
        {
            var session = Volatile.Read(ref current);
            if ((session?.RunId ?? LastRun) is not { } run)
            {
                return [];
            }

            IStorage storage = session?.Storage
                ?? LocalStorage.OpenAsync(shared.Load(parse, host).Options, shared.Directory(parse, host), CancellationToken.None).GetAwaiter().GetResult();
            var history = storage.Tasks.ReadAsync(null, run, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return [.. history.SelectMany(change => change.Tasks).Select(task => task.Id).Distinct()];
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return []; // completion only suggests; the command itself says what is wrong
        }
    }

    /// <summary>The agents the run's commands name: the configuration's, and in a team the ids of its agents, such as <c>developer[1]</c>.</summary>
    internal static IEnumerable<string> Agents(OfficinaOptions options, string agent) =>
        options.Agents.Keys.Concat(options.Agents[agent].Pattern.Roles.SelectMany(role => Enumerable.Range(1, role.Value.Max).Select(number => $"{role.Key}[{number}]")))
            .Order(StringComparer.Ordinal);

    /// <summary>A snapshot of the session's runs, the latest first, for completion.</summary>
    private List<string> RunsLatestFirst()
    {
        lock (runs)
        {
            return Enumerable.Reverse(runs).ToList();
        }
    }

    private static PermissionMode? ModeOf(List<string> words) =>
        words is ["mode", var name] && Enum.TryParse<PermissionMode>(name, ignoreCase: true, out var chosen) ? chosen : null;

    /// <summary>Ctrl+C cancels the reply or the command, or says how to end the session; a second one, or SIGTERM, ends the session.</summary>
    private void Signalled(PosixSignal signal)
    {
        CancellationTokenSource? reply;
        lock (gate)
        {
            if (signal != PosixSignal.SIGINT || armed)
            {
                End();
                return;
            }

            armed = true;
            Interlocked.Exchange(ref interrupted, host.Time.GetTimestamp());
            reply = replying;
        }

        if (reply is null)
        {
            status.WriteLine("Press Ctrl+C again, or type /quit, to end the session.");
            return;
        }

        status.WriteLine("Cancelling. Press Ctrl+C again to end the session.");

        // Off the signal's thread: cancelling runs the reply's cancellation callbacks, which the signal need not wait for.
        _ = Task.Run(() =>
        {
            try
            {
                reply.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended meanwhile.
            }
        });
    }

    /// <summary>Ends the session: a reply or command that runs is cancelled, and the messages that wait are dropped.</summary>
    private void End() => _ = ending.CancelAsync();

    /// <summary>Completes once the session was ended and has not stopped within <paramref name="timeout"/>.</summary>
    private async Task ForcedAsync(TimeSpan timeout, CancellationToken done)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ending.Token.Register(() => ended.TrySetResult()))
        {
            await ended.Task.WaitAsync(done);
        }

        await Task.Delay(timeout, host.Time, done);
    }

    /// <summary>
    /// The console of a <c>sof</c> command typed in the session: the session's lines, taken only while the command reads, and a read
    /// is cancelled when the command stops reading, so no line is left behind to a command that has ended.
    /// </summary>
    private sealed class SessionReader(ChannelReader<string> lines, System.Collections.Concurrent.ConcurrentQueue<string> returned) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = await ChatSession.ReadAsync(lines, cancellationToken);
            if (line is not null && cancellationToken.IsCancellationRequested)
            {
                // The command stopped reading as the line came: it is the session's.
                returned.Enqueue(line);
                return null;
            }

            return line is not null && line.StartsWith('/') ? line[1..] : line;
        }

        public override string? ReadLine() => ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }
}
