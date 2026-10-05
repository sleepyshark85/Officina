using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>The cost limits of the console (APP-14), in US dollars: each reply's, and each session's over all its replies.</summary>
public sealed record Budgets(decimal Reply, decimal Session)
{
    public static Budgets Default { get; } = new(Reply: 0.50m, Session: 5m);
}

/// <summary>
/// The console (ARCHITECTURE §12.1): asks who is using it, then reads messages and commands, streams each reply with its
/// tool activity, asks approval for changes, and cancels a reply on request (APP-01, APP-03, APP-06, APP-13). It is also
/// the agent's approver: the run announces each approval in its event stream, the console asks the staff member there,
/// in order with everything shown before it, and hands the answer to the waiting run. Each reply is a span of its own, the
/// parent of the run's trace, so the console's logs join that trace (APP-20); <c>/audit</c> reads <paramref name="audit"/>
/// and links each run to its trace on <paramref name="dashboard"/> (APP-16). Each conversation is a session, saved in
/// <paramref name="sessions"/> after every step of a reply (APP-10); each reply ends with a status line of its tokens and
/// cost, and stops when it reaches its own or its session's budget (APP-14). A session left with <c>/new</c>,
/// <c>/resume</c> or <c>/quit</c> is summarized, and <c>/sessions</c> summarizes the sessions it lists that were left
/// without one, as after a crash (APP-15).
/// Each staff member has a memory scope of their own in <paramref name="memory"/>, which their runs see and
/// <c>/memory</c> shows (APP-11).
/// </summary>
public sealed partial class BookshopConsole(
    TextReader input, TextWriter output, TimeProvider time, bool echoInput, AuditTable audit, SessionStore sessions, Uri dashboard,
    ILogger? logger = null, Budgets? budgets = null, IMemoryStore? memory = null) : IApprover
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

    /// <summary>How many sessions <c>/sessions</c> lists.</summary>
    private const int Listed = 20;

    private readonly ILogger logger = logger ?? NullLogger.Instance;
    private readonly Budgets budgets = budgets ?? Budgets.Default;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<Approval>> approvals = new();
    private CancellationTokenSource? reply;
    private Task<string?>? pendingRead;
    private AgentDefinition? summarizer;

    /// <summary>The sessions whose summary failed in this console, which <c>/sessions</c> does not try again.</summary>
    private readonly HashSet<string> unsummarized = [];
    private bool atLineStart = true;

    /// <summary>Cancels the reply in progress (APP-03); false when there is none.</summary>
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
    public async Task RunAsync(AgentDefinition agent, AgentDefinition? summarizer = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        this.summarizer = summarizer;
        await output.WriteLineAsync("Bookshop Assistant. Type /help for commands.");
        var staffMember = await AskStaffMemberAsync();
        if (staffMember is null)
        {
            return;
        }

        var session = Session.New(staffMember);
        await output.WriteLineAsync($"Session {session.Id}.");
        while (await ReadAsync("you> ") is { } line)
        {
            switch (line.Trim())
            {
                case "":
                    continue;
                case "/quit":
                    await LeaveAsync(session);
                    return;
                case "/help":
                    await output.WriteLineAsync(Help);
                    continue;
                case "/new":
                    await LeaveAsync(session);
                    session = Session.New(staffMember);
                    await output.WriteLineAsync($"New session {session.Id}.");
                    continue;
                case "/sessions":
                    await ListSessionsAsync(session);
                    continue;
                case "/cost":
                    await output.WriteLineAsync(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Session {session.Id}: {Tokens(session.Usage)}; cost ${session.Cost:0.0000} of its ${budgets.Session:0.00} budget."));
                    continue;
                case { } command when Argument(command, "/resume") is { } id:
                    if (await ResumeAsync(agent, id) is { } resumed)
                    {
                        // Resuming the session in use does not leave it, nor forget that it changed.
                        if (resumed.Id == session.Id)
                        {
                            resumed.Changed = session.Changed;
                        }
                        else
                        {
                            await LeaveAsync(session);
                        }

                        session = resumed;
                    }

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

            // The run context is sent at the start of the session and again only when it changes (APP-13).
            var current = BookshopAgent.Context(time.GetLocalNow(), staffMember);
            await ReplyAsync(agent, session, line, current == session.Context ? null : current, MemoryScope(staffMember));
        }

        await LeaveAsync(session);
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

    /// <summary>What follows <paramref name="name"/> in <paramref name="command"/>, trimmed; null when it is another command.</summary>
    private static string? Argument(string command, string name) =>
        command == name || command.StartsWith(name + " ", StringComparison.Ordinal) ? command[name.Length..].Trim() : null;

    private async Task ListSessionsAsync(Session current)
    {
        try
        {
            var listed = await sessions.ListAsync(Listed, CancellationToken.None);
            var summaries = new Dictionary<string, SessionSummary>();
            var stale = listed.Where(each => each.Stale && each.Id != current.Id && summarizer is not null && !unsummarized.Contains(each.Id)).ToList();
            if (stale.Count > 0)
            {
                await output.WriteLineAsync($"Summarizing {stale.Count} session{(stale.Count == 1 ? "" : "s")} left without a summary…");
            }

            foreach (var left in stale)
            {
                if (await sessions.LoadAsync(left.Id, CancellationToken.None) is { } stored && await SummarizeAsync(left.Id, stored.Conversation) is { } summary)
                {
                    summaries[left.Id] = summary;
                }
            }

            await output.WriteLineAsync(listed.Count == 0 ? "No sessions yet." : "Sessions, most recent first:");
            foreach (var each in listed)
            {
                var (title, text, changes) = summaries.TryGetValue(each.Id, out var fresh)
                    ? (fresh.Title, fresh.Summary, fresh.Changes)
                    : (each.Title, each.Summary, each.Changes);
                await output.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(each.Id == current.Id ? "*" : " ")} {each.Id}  {TimeZoneInfo.ConvertTime(each.Updated, time.LocalTimeZone):ddd d MMM HH:mm}  {each.StaffMember}  {title ?? "(no title yet)"}  ${each.Cost:0.0000}"));
                if (text is not null)
                {
                    await output.WriteLineAsync($"    {text}");
                }

                if (changes.Count > 0)
                {
                    await output.WriteLineAsync($"    Changes: {string.Join("; ", changes)}");
                }
            }
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException)
        {
            await output.WriteLineAsync($"The sessions could not be read: {exception.Message}");
        }
    }

    /// <summary>
    /// The stored session <paramref name="id"/>, to go on with; null when there is none, it cannot be read, or the agent
    /// has changed since it started, which would fail its next reply with a prefix mismatch (APP-10, CTX-04).
    /// </summary>
    private async Task<Session?> ResumeAsync(AgentDefinition agent, string id)
    {
        if (id.Length == 0)
        {
            await output.WriteLineAsync("Which session? Type /resume <id>; /sessions lists them.");
            return null;
        }

        StoredSession? stored;
        try
        {
            stored = await sessions.LoadAsync(id, CancellationToken.None);
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException)
        {
            await output.WriteLineAsync($"The session could not be read: {exception.Message}");
            return null;
        }

        if (stored is null)
        {
            await output.WriteLineAsync($"There is no session {id}. Type /sessions to list them.");
            return null;
        }

        if (!agent.CanContinue(stored.Conversation))
        {
            await output.WriteLineAsync($"Session {id} was started with another version of the assistant, so it cannot go on. Type /new to start a new session.");
            return null;
        }

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture, $"Resumed session {id}: {stored.Conversation.Messages.Length} messages, ${stored.Cost:0.0000} so far."));
        return new Session(stored.Conversation, stored.StaffMember)
        {
            Stored = true,
            Usage = stored.Usage,
            Cost = stored.Cost,
            Context = stored.Conversation.Messages.LastOrDefault(message => message.Role == Role.Operator)?.Text,
        };
    }

    /// <summary>
    /// Summarizes <paramref name="session"/> as it is left (APP-15), unless nothing was said in it since this console took
    /// it up.
    /// </summary>
    private async Task LeaveAsync(Session session)
    {
        if (summarizer is not null && session.Changed && await SummarizeAsync(session.Id, session.Conversation) is { } summary)
        {
            await WriteLineAsync($"Session {session.Id} summarized: {summary.Title}");
        }
    }

    /// <summary>
    /// Runs the summarizer on a session's conversation and stores its summary; returns it, or null when the run or the
    /// store failed, which is told.
    /// </summary>
    private async Task<SessionSummary?> SummarizeAsync(string id, Conversation conversation)
    {
        var result = await summarizer!.RunAsync(SessionSummarizer.Transcript(conversation));
        if (result is not Completed { Output: SessionSummary summary })
        {
            var reason = result switch
            {
                Failed failed => failed.Error,
                Stopped stopped => $"stopped: {stopped.Reason}",
                _ => "no summary",
            };
            unsummarized.Add(id);
            LogSummaryFailed(logger, id, reason);
            await WriteLineAsync($"[Session {id} could not be summarized: {reason}]");
            return null;
        }

        try
        {
            await sessions.SaveSummaryAsync(id, summary, CancellationToken.None);
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException)
        {
            LogSummaryFailed(logger, id, exception.Message);
            await WriteLineAsync($"[The summary of session {id} could not be saved: {exception.Message}]");
        }

        return summary;
    }

    /// <summary>
    /// A staff member's memory scope: their name, so it is the same whatever case they type it in. Memory follows the staff
    /// member at the counter, not a session's owner: a session resumed by another member runs in that member's scope.
    /// </summary>
    private static string MemoryScope(string staffMember) => staffMember.Trim().ToLowerInvariant();

    /// <summary>Shows every file of the staff member's memory, with its text (APP-11).</summary>
    private async Task ShowMemoryAsync(string scope)
    {
        try
        {
            var files = memory is null ? [] : (await memory.ListAsync(scope, CancellationToken.None)).OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
            await output.WriteLineAsync(files.Count == 0 ? "Nothing remembered yet." : "Remembered:");
            foreach (var file in files)
            {
                await output.WriteLineAsync($"{MemoryTool.Root}/{file.Path}");
                foreach (var line in (await memory!.ReadAsync(scope, file.Path, CancellationToken.None) ?? "").TrimEnd('\n').Split('\n'))
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
    /// Streams one reply, saving the session after every step of it, within the reply's budget or what is left of the
    /// session's, whichever is less.
    /// </summary>
    private async Task ReplyAsync(AgentDefinition agent, Session session, string message, string? context, string memoryScope)
    {
        using var span = Source.StartActivity("reply");
        using var cancellation = new CancellationTokenSource();
        Volatile.Write(ref reply, cancellation);
        var conversation = session.Conversation;
        var left = budgets.Session - session.Cost;
        var budgeted = agent with { Budget = new Budget { Cost = Math.Max(0, Math.Min(budgets.Reply, left)) } };
        var (saveFailed, labelled, compacted) = (false, false, false);

        // What the reply has spent so far, from each model call's usage, so every save stores the session's whole spend.
        var (spent, spentCost) = (default(Usage), 0m);
        try
        {
            await foreach (var runEvent in budgeted.StreamAsync(conversation, message, context, memoryScope, cancellation.Token))
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
                        saveFailed |= !await SaveAsync(session, session.Usage + spent, session.Cost + spentCost, saveFailed);
                        break;
                    case RunEnded { Result: var result }:
                        (session.Usage, session.Cost) = (session.Usage + result.Usage, session.Cost + result.Cost);
                        await SaveAsync(session, session.Usage, session.Cost, saveFailed);
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
                            Stopped { Reason: StopReason.Budget } when left <= budgets.Reply => string.Create(
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

    /// <summary>The status line after a reply (APP-14): its tokens, the share of input read from the cache, its cost and the session's.</summary>
    private static string StatusLine(RunResult result, Session session) => string.Create(
        CultureInfo.InvariantCulture, $"[{Tokens(result.Usage)} · reply ${result.Cost:0.0000} · session ${session.Cost:0.0000}]");

    private static string Tokens(Usage usage)
    {
        var input = usage.Input + usage.CacheRead + usage.CacheWrite;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"tokens: {input:N0} in ({(input == 0 ? 0 : (double)usage.CacheRead / input):0%} from cache), {usage.Output:N0} out");
    }

    /// <summary>
    /// Saves the session with its totals so far; returns whether it was saved. A failure is told once a reply
    /// (<paramref name="told"/>), and the reply goes on: the next save stores the whole conversation and totals.
    /// </summary>
    private async Task<bool> SaveAsync(Session session, Usage usage, decimal cost, bool told)
    {
        try
        {
            await sessions.SaveAsync(session.Conversation, session.StaffMember, usage, cost, session.Stored, CancellationToken.None);
            session.Stored = true;
            return true;
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
        {
            LogSaveFailed(logger, session.Id, exception.Message);
            if (!told)
            {
                await WriteLineAsync($"[The session could not be saved: {exception.Message}]");
            }

            return false;
        }
    }

    /// <summary>Shows the call's exact input and asks the staff member (APP-06); the waiting run gets the answer.</summary>
    /// <remarks>
    /// Cancelling the reply at the prompt takes effect at once: the run stops waiting for the answer, and the line being
    /// typed becomes the next message. A cancelled prompt leaves no answer behind.
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

        // The console's reader blocks, so the read runs aside; a read that a cancel abandons serves the next prompt.
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
        // pending so it serves the next prompt rather than being lost.
        if (cancellationToken.IsCancellationRequested)
        {
            atLineStart = false;
            return null;
        }

        pendingRead = null;
        if (echoInput)
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {Session} could not be summarized: {Error}")]
    private static partial void LogSummaryFailed(ILogger logger, string session, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {Session} could not be saved: {Error}")]
    private static partial void LogSaveFailed(ILogger logger, string session, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reply in conversation {Conversation} failed ({Reason}): {Error}")]
    private static partial void LogReplyFailed(ILogger logger, string conversation, FailureReason reason, string error);

    /// <summary>The session in use: its conversation, who started it, what its replies used, and the run context it last received.</summary>
    private sealed class Session(Conversation conversation, string staffMember)
    {
        public Conversation Conversation { get; } = conversation;

        public string Id => Conversation.Id;

        public string StaffMember { get; } = staffMember;

        public Usage Usage { get; set; }

        public decimal Cost { get; set; }

        public string? Context { get; set; }

        /// <summary>How many tool calls the last clearing line named, so a clearing the provider repeats is shown once.</summary>
        public int? ClearedToolCalls { get; set; }

        /// <summary>Whether the session has a row in the store yet.</summary>
        public bool Stored { get; set; }

        /// <summary>Whether the conversation grew since this console took it up, so leaving it needs a new summary.</summary>
        public bool Changed { get; set; }

        /// <summary>A new session, with an id short enough to type in <c>/resume</c>; one that collides fails its first save.</summary>
        public static Session New(string staffMember) => new(new Conversation { Id = Guid.NewGuid().ToString("N")[..12] }, staffMember);
    }
}
