using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Reports;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Storage.Sqlite;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// One <c>sof chat</c> session. The console is read on a thread of its own, as a read blocks its thread, and the session takes
/// each line from <see cref="lines"/>. Each message is a run, opened and closed as <c>sof run</c>'s is; while it runs, a command
/// is carried out at once and a message waits until the reply ends. Ctrl+C cancels the reply; a second Ctrl+C, <c>/quit</c> or
/// the end of the input ends the session, the end of the input once the messages it sent have their replies.
/// </summary>
[SuppressMessage("Reliability", "CA1001", Justification = "The session's token source has no timer, so it holds nothing to dispose, and a signal may still cancel it as the process ends.")]
internal sealed class ChatSession
{
    private static readonly string Help = $"""
        {RunCommand.Help("/")}
                  | /report | /quit (or Ctrl+D)
        A line that does not start with / is a message. While a reply runs, a message waits until it ends; /tell reaches an agent at once.
        """;

    private readonly ParseResult parse;
    private readonly ConfigurationCommandOptions shared;
    private readonly SofEnvironment host;
    private readonly string? named;
    private readonly Channel<string> lines = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
    private readonly Queue<string> queued = new();
    private readonly CancellationTokenSource ending = new();
    private readonly Lock gate = new();
    private readonly OwnerQueue owner;
    private readonly StatusView status;

    /// <summary>Whether the next message starts a new conversation (<c>--new</c>).</summary>
    private bool fresh;

    /// <summary>The reply that runs now, which Ctrl+C cancels.</summary>
    private CancellationTokenSource? replying;

    /// <summary>Whether the next Ctrl+C ends the session: one was pressed after the last message was sent.</summary>
    private bool armed;

    private bool inputEnded;
    private string agent = "";
    private PermissionMode? mode;
    private string? lastRun;
    private RunCommand.Session? current;

    public ChatSession(ParseResult parse, ConfigurationCommandOptions shared, SofEnvironment host, string? named, bool fresh)
    {
        (this.parse, this.shared, this.host, this.named, this.fresh) = (parse, shared, host, named, fresh);
        var output = TextWriter.Synchronized(host.Out);
        owner = new OwnerQueue(output, "/");
        status = new StatusView(output, () => current?.Workspace?.Queue, stream: true);
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var configuration = shared.Load(parse, host);
        if (ConfigurationCommandOptions.ReportErrors(configuration, host) is var code && code != ExitCodes.Success)
        {
            return code;
        }

        if (ConfigurationCommandOptions.Agent(named, configuration.Options, host) is not { } name)
        {
            return ExitCodes.Usage;
        }

        if (ChatCommand.Refusal(configuration, name) is { } refusal)
        {
            host.Error.WriteLine($"error: {refusal}");
            return ExitCodes.Invalid;
        }

        agent = name;
        var options = ChatCommand.Conversing(configuration, name);
        var keeps = ChatCommand.KeepsHistory(options, name);
        status.WriteLine($"Chatting with {name}. Each line you type is a message; /help lists the commands, and /quit or Ctrl+D ends the session.");
        if (options.Agents[keeps].Context.History.Strategy == HistoryStrategy.None)
        {
            status.WriteLine($"note: agents.{keeps}.context.history.strategy is none, so each message starts a new conversation.");
            fresh = false;
        }
        else if (fresh)
        {
            status.WriteLine("Your first message starts a new conversation.");
        }

        using var signals = host.Signals?.Invoke(Signalled);
        using var cancelled = ct.Register(End);
        // A read from the console blocks its thread and cannot be cancelled, so the console is read on a thread of its own, which
        // is left to end with the process.
        _ = Task.Run(ReadConsoleAsync, CancellationToken.None);
        using var done = new CancellationTokenSource();
        var chatting = ChatAsync();
        var forced = ForcedAsync(SofCommandLine.TerminationTimeout(options), done.Token);
        var first = await Task.WhenAny(chatting, forced);
        await done.CancelAsync();
        if (first == chatting)
        {
            return await chatting;
        }

        // As the command line does for sof run: what has not stopped this long after the session was ended is left to end with the process.
        host.Error.WriteLine("error: the session did not end in time, so it is left to end with the process.");
        return ExitCodes.NotCompleted;
    }

    /// <summary>Takes the owner's lines until the session ends: each command is carried out and each message sent.</summary>
    private async Task<int> ChatAsync()
    {
        try
        {
            while (await NextAsync() is { } line)
            {
                if (line.StartsWith('/'))
                {
                    try
                    {
                        await IdleAsync(line[1..].Trim());
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // A command that fails says so, and the owner can go on typing.
                        status.WriteLine($"error: {exception.Message}");
                    }
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
            host.Error.WriteLine($"error: the session failed: {exception.Message}");
            return ExitCodes.NotCompleted;
        }
    }

    /// <summary>The next line to act on: a message that waited for the last reply first, then the owner's next line; null once the session ends.</summary>
    private async Task<string?> NextAsync()
    {
        if (ending.IsCancellationRequested)
        {
            return null;
        }

        if (queued.TryDequeue(out var message))
        {
            return message;
        }

        try
        {
            var line = inputEnded ? null : await ReadAsync(ending.Token);
            inputEnded = line is null;
            return line;
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The owner's next line, or null at the end of the input.</summary>
    private async Task<string?> ReadAsync(CancellationToken ct)
    {
        while (await lines.Reader.WaitToReadAsync(ct))
        {
            if (lines.Reader.TryRead(out var line))
            {
                return line;
            }
        }

        return null;
    }

    private async Task ReadConsoleAsync()
    {
        try
        {
            while (await host.In.ReadLineAsync(CancellationToken.None) is { } line)
            {
                lines.Writer.TryWrite(line);
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
    private async Task SendAsync(string message)
    {
        CancellationTokenSource reply;
        lock (gate)
        {
            armed = false;
            replying = reply = CancellationTokenSource.CreateLinkedTokenSource(ending.Token);
        }

        try
        {
            var work = new Work(agent, message) { Trigger = Trigger.Conversation };
            var code = await RunCommand.ExecuteAsync(
                parse, shared, host, work.RunId, agent, existing: false, leaveWorkingCopies: false, (session, token) => ReplyAsync(session, work, token), reply.Token,
                Trigger.Conversation, owner);
            if (code != ExitCodes.Success && !ending.IsCancellationRequested)
            {
                status.WriteLine("The message was not sent.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ending.IsCancellationRequested)
        {
            // The session goes on: the next message opens everything again.
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

        lastRun = session.RunId;
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
    /// <c>--new</c>: an empty shortened turn holds the whole conversation, which is nothing yet, so the next turn starts a new one
    /// with the project memory of now (CAP-05, MEM-03). The earlier turns stay for the runs that wrote them.
    /// </summary>
    private async Task NewConversationAsync(RunCommand.Session session, CancellationToken ct)
    {
        var options = session.Runner.Options;
        var memory = options.Capabilities.ProjectMemory.Enabled ? (await session.Runner.Memory(Caller.Anonymous).ReadAsync(ct)).Revision : 0;
        var keeps = ChatCommand.KeepsHistory(options, session.Agent);
        await session.Storage.Conversations.AppendAsync(null, new ConversationTurn(keeps, null, host.Time.GetUtcNow(), [], true, memory, memory, session.RunId), ct);
    }

    /// <summary>Takes the owner's lines while a reply runs: a command is carried out on the run, and a message waits for the reply to end.</summary>
    private async Task DuringAsync(RunCommand.Session session, CancellationToken stop)
    {
        try
        {
            while (await ReadAsync(stop) is { } line)
            {
                if (!line.StartsWith('/'))
                {
                    if (line.Trim() is { Length: > 0 } message)
                    {
                        queued.Enqueue(message);
                        status.WriteLine("(it is sent when this reply ends; /tell <agent> <text> reaches an agent now)");
                    }

                    continue;
                }

                var command = line[1..].Trim();
                switch (command.Split(' ', 2)[0])
                {
                    case "quit":
                        End();
                        break;
                    case "report":
                        await ReportAsync(session.Storage, session.RunId, ending.Token);
                        break;
                    case "help":
                        status.WriteLine(Help);
                        break;
                    default:
                        await RunCommand.CarryOutAsync(command, session, status, Help, ending.Token); // a command once started is carried out, even if the reply ends meanwhile
                        mode = ModeOf(command) ?? mode;
                        break;
                }
            }

            inputEnded = true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>A command between replies: those that act on a run say that none runs.</summary>
    private async Task IdleAsync(string command)
    {
        var word = command.Split(' ', 2)[0];
        switch (word)
        {
            case "quit":
                End();
                break;
            case "status":
                status.Print(owner);
                break;
            case "mode" when ModeOf(command) is { } chosen:
                mode = chosen;
                break;
            case "report" when lastRun is { } run:
                await ReportAsync(await SqliteStorage.OpenAsync(Path.Combine(shared.Directory(parse, host), WorkspaceOptions.StateFolder, "sof.db"), ending.Token), run, ending.Token);
                break;
            case "report":
                status.WriteLine("error: no message has been sent in this session yet.");
                break;
            case "approve" or "deny" or "change" or "answer" or "tell" or "pause" or "resume" or "cancel" or "checkpoint" or "board" or "memory":
                status.WriteLine($"error: no reply is running; /{word} works while one does.");
                break;
            default:
                status.WriteLine(Help);
                break;
        }
    }

    /// <summary>RUN-11: the report of a message's run.</summary>
    private async Task ReportAsync(IStorage storage, string runId, CancellationToken ct)
    {
        if (await RunReport.BuildAsync(storage, null, runId, ct) is { } report)
        {
            status.WriteLine(report.ToText().TrimEnd());
        }
    }

    private static PermissionMode? ModeOf(string command) =>
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries) is ["mode", var name] && Enum.TryParse<PermissionMode>(name, ignoreCase: true, out var chosen) ? chosen : null;

    /// <summary>Ctrl+C cancels the reply, or says how to end the session; a second one, or SIGTERM, ends the session.</summary>
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
            reply = replying;
        }

        if (reply is null)
        {
            status.WriteLine("Press Ctrl+C again, or type /quit, to end the session.");
            return;
        }

        status.WriteLine("Cancelling the reply. Press Ctrl+C again to end the session.");
        try
        {
            reply.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The reply ended meanwhile.
        }
    }

    /// <summary>Ends the session: a reply that runs is cancelled, and the messages that wait are dropped.</summary>
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
}
