using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>
/// The console: asks who is using it, reads messages and commands, streams each reply with its tool activity, asks
/// approval for changes and cancels a reply on request. It is the agent's approver: it prompts on each approval event,
/// in order with what was shown before, and hands the answer to the waiting run. Each reply is a span, the parent of the
/// run's trace, so the console's logs join it; <c>/audit</c> reads <paramref name="audit"/> and links each run to its
/// trace on the dashboard in <paramref name="settings"/>. Each conversation is a session, saved in <paramref name="store"/> after
/// every step; each reply ends with a status line of tokens and cost, and stops at its own or its session's budget. A
/// session left with <c>/new</c>, <c>/resume</c> or <c>/quit</c> is summarized, and <c>/sessions</c> summarizes those
/// left without one, as after a crash. Each staff member has their own memory scope in <paramref name="memory"/>, which
/// <c>/memory</c> shows.
/// </summary>
public sealed partial class BookshopConsole(
    Terminal terminal, TimeProvider time, AuditTable audit, SessionStore store, BookshopSettings settings, Budgets budgets, IMemoryStore memory,
    ILogger<BookshopConsole> logger) : IApprover
{
    /// <summary>The name of the console's activity source, for the exporter to listen to.</summary>
    public const string SourceName = "BookshopAssistant";

    private const string Help = """
        Commands:
          /help           Show this help.
          /new            Start a new session.
          /sessions       List the latest sessions.
          /resume <id>    Go on with the session with that id.
          /cost           Show this session's tokens and cost.
          /audit [<id>]   Show the audit trail of this session, or of the session with that id.
          /memory         Show what the assistant remembers for you.
          /quit           Leave the assistant.
        Anything else is a message to the assistant. Ctrl+C stops a reply in progress.
        """;

    private static readonly ActivitySource Source = new(SourceName);

    private readonly TextReader input = terminal.Input;
    private readonly TextWriter output = terminal.Output;
    private readonly Uri dashboard = settings.DashboardUrl;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Approval>> approvals = new();
    private CancellationTokenSource? reply;
    private Task<string?>? pendingRead;
    private bool atLineStart = true;

    /// <summary>Cancels the reply in progress; false when there is none.</summary>
    public bool CancelReply()
    {
        var current = Volatile.Read(ref reply);
        if (current is null)
        {
            return false;
        }

        try
        {
            current.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The reply ended between the read and the cancel.
            return false;
        }
    }

    /// <summary>
    /// Runs the session until <c>/quit</c> or the end of input. <paramref name="agent"/> must have this console as its
    /// approver; without a <paramref name="summarizer"/>, sessions are not summarized.
    /// </summary>
    public async Task RunAsync(Agent agent, Agent? summarizer = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var sessions = new SessionManager(store, summarizer, budgets, logger, WriteLineAsync);
        await output.WriteLineAsync("Bookshop Assistant. Type /help for commands.");
        var staffMember = await AskStaffMemberAsync();
        if (staffMember is null)
        {
            return;
        }

        var session = SessionManager.New(staffMember);
        await output.WriteLineAsync($"Session {session.Id}.");
        while (await ReadAsync("you> ") is { } line)
        {
            switch (line.Trim())
            {
                case "":
                    continue;
                case "/quit":
                    await sessions.LeaveAsync(session);
                    return;
                case "/help":
                    await output.WriteLineAsync(Help);
                    continue;
                case "/new":
                    await sessions.LeaveAsync(session);
                    session = SessionManager.New(staffMember);
                    await output.WriteLineAsync($"New session {session.Id}.");
                    continue;
                case "/sessions":
                    await ListSessionsAsync(sessions, session);
                    continue;
                case "/cost":
                    await output.WriteLineAsync(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Session {session.Id}: {Tokens(session.Usage)}; cost ${session.Cost:0.0000} of its ${budgets.Session:0.00} budget."));
                    continue;
                case { } command when Argument(command, "/resume") is { } id:
                    session = await sessions.ResumeAsync(agent, id, session);
                    continue;
                case { } command when Argument(command, "/audit") is { } id:
                    await ShowAuditAsync(id.Length > 0 ? id : session.Id);
                    continue;
                case "/memory":
                    await ShowMemoryAsync(MemoryScope(staffMember));
                    continue;
                case ['/', ..] command:
                    await output.WriteLineAsync($"Unknown command {command}. Type /help for commands.");
                    continue;
            }

            // The run context is sent at the start of the session and again only when it changes.
            var current = BookshopAgent.Context(time.GetLocalNow(), staffMember);
            await ReplyAsync(agent, sessions, session, line, current == session.Context ? null : current, MemoryScope(staffMember));
        }

        await sessions.LeaveAsync(session);
    }

    public async Task<Approval> ApproveAsync(Tool tool, ToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var answer = approvals.GetOrAdd(toolCall.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            return await answer.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            approvals.TryRemove(toolCall.Id, out _);
        }
    }

    private async Task<string?> AskStaffMemberAsync()
    {
        while (await ReadAsync("Who is using the assistant? Your name: ") is { } name)
        {
            if (!string.IsNullOrWhiteSpace(name) && !MemoryPath.IsValidScope(MemoryScope(name)))
            {
                await output.WriteLineAsync("That name cannot be used. Please give another name.");
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                await output.WriteLineAsync($"Hello, {name.Trim()}.");
                return name.Trim();
            }
        }

        return null;
    }

    /// <summary>What follows <paramref name="name"/> in <paramref name="command"/>, trimmed; null for another command.</summary>
    private static string? Argument(string command, string name) =>
        command == name || command.StartsWith(name + " ", StringComparison.Ordinal) ? command[name.Length..].Trim() : null;

    private async Task ListSessionsAsync(SessionManager sessions, Session current)
    {
        if (await sessions.ListAsync(current) is not { } listed)
        {
            return;
        }

        await WriteLineAsync(listed.Count == 0 ? "No sessions yet." : "Sessions, most recent first:");
        foreach (var each in listed)
        {
            await WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{(each.Id == current.Id ? "*" : " ")} {each.Id}  {TimeZoneInfo.ConvertTime(each.Updated, time.LocalTimeZone):ddd d MMM HH:mm}  {each.StaffMember}  {each.Title ?? "(no title yet)"}  ${each.Cost:0.0000}"));
            if (each.Summary is not null)
            {
                await WriteLineAsync($"    {each.Summary}");
            }

            if (each.Changes.Count > 0)
            {
                await WriteLineAsync($"    Changes: {string.Join("; ", each.Changes)}");
            }
        }
    }

    /// <summary>
    /// A staff member's memory scope: their name, so case does not matter. Memory follows who is at the counter, not who
    /// owns the session: a session resumed by another member runs in that member's scope.
    /// </summary>
    private static string MemoryScope(string staffMember) => staffMember.Trim().ToLowerInvariant();

    /// <summary>Shows every file of the staff member's memory, with its text.</summary>
    private async Task ShowMemoryAsync(string scope)
    {
        try
        {
            var files = (await memory.ListAsync(scope, CancellationToken.None)).OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
            await output.WriteLineAsync(files.Count == 0 ? "Nothing remembered yet." : "Remembered:");
            foreach (var file in files)
            {
                await output.WriteLineAsync($"{MemoryTool.Root}/{file.Path}");
                foreach (var line in (await memory.ReadAsync(scope, file.Path, CancellationToken.None) ?? "").TrimEnd('\n').Split('\n'))
                {
                    await output.WriteLineAsync($"  {line}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"The memory could not be read: {exception.Message}");
        }
    }

    private async Task ShowAuditAsync(string session)
    {
        try
        {
            var entries = await audit.ReadAsync(session, CancellationToken.None);
            await output.WriteLineAsync(AuditView.Format(session, entries, dashboard, time.LocalTimeZone));
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException)
        {
            await output.WriteLineAsync($"The audit trail could not be read: {exception.Message}");
        }
    }

    /// <summary>
    /// Streams one reply, saving the session after every step, within the lower of the reply's budget and what is left of
    /// the session's.
    /// </summary>
    private async Task ReplyAsync(Agent agent, SessionManager sessions, Session session, string message, string? context, string memoryScope)
    {
        using var span = Source.StartActivity("reply");
        using var cancellation = new CancellationTokenSource();
        Volatile.Write(ref reply, cancellation);
        var conversation = session.Conversation;
        var options = sessions.Options(session, context, memoryScope);
        var sessionLimits = sessions.SessionBudgetLimits(session);
        var (saveFailed, labelled, compacted) = (false, false, false);

        // What the reply has spent so far, from each model call's usage, so every save stores the session's whole spend.
        var (spent, spentCost) = (default(Usage), 0m);
        try
        {
            await foreach (var runEvent in agent.StreamAsync(conversation, message, options, cancellation.Token))
            {
                switch (runEvent)
                {
                    case TextStreamed text:
                        if (!labelled)
                        {
                            await WriteLineAsync("");
                            await WriteAsync("assistant> ");
                            labelled = true;
                        }

                        await WriteAsync(text.Text);
                        break;
                    case ReplyRestarted:
                        await WriteLineAsync("[The reply was interrupted and starts again.]");
                        break;
                    case ToolCallStarted started:
                        await WriteLineAsync($"  > {started.Call.Name} {started.Call.Input}");
                        break;
                    case ApprovalAsked asked:
                        await AskApprovalAsync(asked.Call, cancellation.Token);
                        break;
                    case ConversationCompacted compaction:
                        compacted = true;
                        await WriteLineAsync(string.Create(
                            CultureInfo.InvariantCulture, $"  ~ Conversation compacted: {compaction.Tokens:N0} tokens summarized into {compaction.SummaryTokens:N0}."));
                        break;

                    // The provider clears again on every call, as each resends the whole conversation: the line shows only a change.
                    case ToolResultsCleared cleared when cleared.ToolCalls != session.ClearedToolCalls:
                        session.ClearedToolCalls = cleared.ToolCalls;
                        await WriteLineAsync(string.Create(
                            CultureInfo.InvariantCulture, $"  ~ Old tool results cleared: {cleared.ToolCalls} tool calls, {cleared.Tokens:N0} tokens."));
                        break;
                    case ToolCallFinished { Result: var result } finished:
                        await WriteLineAsync(result.IsError
                            ? $"  < {finished.Call.Name}: error: {FirstLine(result.Content)}"
                            : $"  < {finished.Call.Name}: ok");
                        break;
                    case UsageReported reported:
                        (spent, spentCost) = (spent + reported.Usage, spentCost + reported.Cost);
                        break;
                    case ConversationAppended { Message: var appended }:
                        session.Changed = true;
                        session.Context = appended.Role == Role.Operator ? appended.Text : session.Context;
                        saveFailed |= !await sessions.SaveAsync(session, session.Usage + spent, session.Cost + spentCost, saveFailed);
                        break;
                    case RunEnded { Result: var result }:
                        (session.Usage, session.Cost) = (session.Usage + result.Usage, session.Cost + result.Cost);
                        await sessions.SaveAsync(session, session.Usage, session.Cost, saveFailed);
                        LogReplyEnded(logger, conversation.Id, result.GetType().Name, result.Usage.Input + result.Usage.CacheRead + result.Usage.CacheWrite, result.Usage.Output);
                        if (result is Failed failure)
                        {
                            LogReplyFailed(logger, conversation.Id, failure.Reason, failure.Error);
                        }

                        await WriteLineAsync(result switch
                        {
                            Completed { Text: var text } when string.IsNullOrWhiteSpace(text) => compacted
                                ? "[The conversation was compacted and the reply has no text. Please ask again.]"
                                : "[The reply has no text. Please ask again.]",
                            Completed => "",
                            Stopped { Reason: StopReason.Cancelled } => "[Cancelled.]",
                            Stopped { Reason: StopReason.Budget } when sessionLimits => string.Create(
                                CultureInfo.InvariantCulture,
                                $"[Stopped: this session has reached its budget of ${budgets.Session:0.00##}. Type /new to start a new session.]"),
                            Stopped { Reason: StopReason.Budget } => string.Create(
                                CultureInfo.InvariantCulture, $"[Stopped: this reply has reached its budget of ${budgets.Reply:0.00##}.]"),
                            Stopped stopped => $"[Stopped: {stopped.Reason}.]",
                            Failed { Reason: FailureReason.PrefixMismatch } =>
                                "[This session was started with another version of the assistant, so it cannot go on. Type /new to start a new session.]",
                            Failed failed => $"[Failed: {failed.Error}]",
                            _ => "",
                        });
                        await WriteLineAsync(StatusLine(result, session));
                        break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref reply, null);
        }

        await EndLineAsync();
    }

    /// <summary>The status line after a reply: its tokens, the share of input from the cache, its cost and the session's.</summary>
    private static string StatusLine(RunResult result, Session session) => string.Create(
        CultureInfo.InvariantCulture, $"[{Tokens(result.Usage)} · reply ${result.Cost:0.0000} · session ${session.Cost:0.0000}]");

    private static string Tokens(Usage usage)
    {
        var input = usage.Input + usage.CacheRead + usage.CacheWrite;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"tokens: {input:N0} in ({(input == 0 ? 0 : (double)usage.CacheRead / input):0%} from cache), {usage.Output:N0} out");
    }

    /// <summary>Shows the call's exact input and asks the staff member; the waiting run gets the answer.</summary>
    /// <remarks>
    /// Cancelling the reply at the prompt takes effect at once: the run stops waiting, the line being typed becomes the next
    /// message, and no answer is left behind.
    /// </remarks>
    private async Task AskApprovalAsync(ToolCall call, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            await WriteLineAsync($"  ? {call.Name} needs your approval. Its exact input:");
            await WriteLineAsync($"    {call.Input}");
            var answer = await ReadAsync("    Approve? [y/N] ", cancellationToken);
            var approval = answer?.Trim().ToUpperInvariant() is "Y" or "YES"
                ? Approval.Granted
                : Approval.Denied("the staff member declined");
            if (!cancellationToken.IsCancellationRequested)
            {
                approvals.GetOrAdd(call.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(approval);
            }
        }

        // A cancelled run no longer waits for the answer: drop any entry its wait or this answer left.
        if (cancellationToken.IsCancellationRequested)
        {
            approvals.TryRemove(call.Id, out _);
        }
    }

    /// <summary>Reads a line: null at the end of input, or when <paramref name="cancellationToken"/> is cancelled first.</summary>
    private async Task<string?> ReadAsync(string prompt, CancellationToken cancellationToken = default)
    {
        await EndLineAsync();
        await output.WriteAsync(prompt);
        await output.FlushAsync(CancellationToken.None);

        // The console's reader blocks, so the read runs aside; a read a cancel abandons serves the next prompt.
        pendingRead ??= Task.Run(async () => await input.ReadLineAsync(), CancellationToken.None);
        string? line;
        try
        {
            line = await pendingRead.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            atLineStart = false;
            return null;
        }

        // The line can finish just as the cancel arrives, and WaitAsync then returns it instead of throwing. Keep it
        // pending for the next prompt rather than lose it.
        if (cancellationToken.IsCancellationRequested)
        {
            atLineStart = false;
            return null;
        }

        pendingRead = null;
        if (terminal.EchoInput)
        {
            await output.WriteLineAsync(line ?? "");
        }

        atLineStart = true;
        return line;
    }

    private async Task WriteAsync(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        await output.WriteAsync(text);
        await output.FlushAsync();
        atLineStart = text.EndsWith('\n');
    }

    private async Task WriteLineAsync(string text)
    {
        await EndLineAsync();
        if (text.Length > 0)
        {
            await output.WriteLineAsync(text);
        }
    }

    private async Task EndLineAsync()
    {
        if (!atLineStart)
        {
            await output.WriteLineAsync();
            atLineStart = true;
        }
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0];

    [LoggerMessage(Level = LogLevel.Information, Message = "Reply in conversation {Conversation} ended {Result}: {InputTokens} input tokens, {OutputTokens} output tokens")]
    private static partial void LogReplyEnded(ILogger logger, string conversation, string result, long inputTokens, long outputTokens);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reply in conversation {Conversation} failed ({Reason}): {Error}")]
    private static partial void LogReplyFailed(ILogger logger, string conversation, FailureReason reason, string error);
}
