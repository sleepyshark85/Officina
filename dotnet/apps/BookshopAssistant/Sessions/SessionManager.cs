using System.Globalization;
using Microsoft.Extensions.Logging;
using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>
/// One console's sessions: starting, resuming, saving and leaving them, their summaries, and their budgets. What the
/// staff member should know goes to <paramref name="tell"/>. Without a <paramref name="summarizer"/>, sessions are not
/// summarized.
/// </summary>
public sealed partial class SessionManager(SessionStore store, Agent? summarizer, Budgets budgets, ILogger logger, Func<string, Task> tell)
{
    /// <summary>How many sessions a listing holds.</summary>
    private const int Listed = 20;

    /// <summary>How many sessions left without a summary one listing summarizes; later listings do the rest.</summary>
    private const int SummariesPerListing = 3;

    /// <summary>The sessions whose summary failed in this console, which listings do not retry.</summary>
    private readonly HashSet<string> unsummarized = [];

    /// <summary>A new session, with an id short enough to type in <c>/resume</c>; one that collides fails its first save.</summary>
    public static Session New(string staffMember) => new(new Conversation { Id = Guid.NewGuid().ToString("N")[..12] }, staffMember);

    /// <summary>
    /// The session to go on with after <c>/resume</c>: the stored session <paramref name="id"/>, leaving
    /// <paramref name="current"/>; or <paramref name="current"/> itself, when the stored one is missing, cannot be read, or
    /// was started by another agent, which would fail its next reply with a prefix mismatch. Resuming the session in use
    /// reloads it without leaving it, and keeps whether it changed.
    /// </summary>
    public async Task<Session> ResumeAsync(Agent agent, string id, Session current)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(current);
        if (await LoadAsync(agent, id) is not { } resumed)
        {
            return current;
        }

        if (resumed.Id == current.Id)
        {
            resumed.Changed = current.Changed;
        }
        else
        {
            await LeaveAsync(current);
        }

        return resumed;
    }

    /// <summary>The stored session <paramref name="id"/>, told as resumed; null, and told why, when it cannot go on.</summary>
    private async Task<Session?> LoadAsync(Agent agent, string id)
    {
        if (id.Length == 0)
        {
            await tell("Which session? Type /resume <id>; /sessions lists them.");
            return null;
        }

        StoredSession? stored;
        try
        {
            stored = await store.LoadAsync(id, CancellationToken.None);
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException or InvalidDataException)
        {
            await tell($"The session could not be read: {exception.Message}");
            return null;
        }

        if (stored is null)
        {
            await tell($"There is no session {id}. Type /sessions to list them.");
            return null;
        }

        if (!agent.CanContinue(stored.Conversation))
        {
            await tell($"Session {id} was started with another version of the assistant, so it cannot go on. Type /new to start a new session.");
            return null;
        }

        await tell(string.Create(
            CultureInfo.InvariantCulture, $"Resumed session {id}: {stored.Conversation.Messages.Length} messages, ${stored.Cost:0.0000} so far."));
        return new Session(stored.Conversation, stored.StaffMember)
        {
            Saved = stored.Saved,
            Usage = stored.Usage,
            Cost = stored.Cost,
            Context = stored.Conversation.Messages.LastOrDefault(message => message.Role == Role.Operator)?.Text,
        };
    }

    /// <summary>Summarizes <paramref name="session"/> as it is left, unless nothing was said since this console took it up.</summary>
    public async Task LeaveAsync(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (summarizer is not null && session.Changed && await SummarizeAsync(session.Id, session.Conversation) is { } summary)
        {
            await tell($"Session {session.Id} summarized: {summary.Title}");
        }
    }

    /// <summary>
    /// The latest sessions, most recent first, after summarizing a few of those left without a summary (never
    /// <paramref name="current"/>); null when the store cannot be read, which is told.
    /// </summary>
    public async Task<IReadOnlyList<SessionListing>?> ListAsync(Session current)
    {
        ArgumentNullException.ThrowIfNull(current);
        try
        {
            var listed = await store.ListAsync(Listed, CancellationToken.None);
            var stale = listed.Where(each => each.Stale && each.Id != current.Id && summarizer is not null && !unsummarized.Contains(each.Id)).ToList();
            var summarizing = stale.Take(SummariesPerListing).ToList();
            if (summarizing.Count > 0)
            {
                await tell(stale.Count > summarizing.Count
                    ? $"Summarizing {summarizing.Count} of {stale.Count} sessions left without a summary; /sessions again does more…"
                    : $"Summarizing {stale.Count} session{(stale.Count == 1 ? "" : "s")} left without a summary…");
            }

            var summaries = new Dictionary<string, SessionSummary>();
            foreach (var left in summarizing)
            {
                StoredSession? stored;
                try
                {
                    stored = await store.LoadAsync(left.Id, CancellationToken.None);
                }
                catch (InvalidDataException exception)
                {
                    unsummarized.Add(left.Id);
                    await tell($"[{exception.Message}]");
                    continue;
                }

                if (stored is not null && await SummarizeAsync(left.Id, stored.Conversation) is { } summary)
                {
                    summaries[left.Id] = summary;
                }
            }

            return [.. listed.Select(each => summaries.TryGetValue(each.Id, out var fresh)
                ? each with { Title = fresh.Title, Summary = fresh.Summary, Changes = fresh.Changes }
                : each)];
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException)
        {
            await tell($"The sessions could not be read: {exception.Message}");
            return null;
        }
    }

    /// <summary>What a reply in <paramref name="session"/> runs with: the lower of the reply's budget and what is left of the session's.</summary>
    public RunOptions Options(Session session, string? context, string memoryScope)
    {
        ArgumentNullException.ThrowIfNull(session);
        var cost = Math.Max(0, Math.Min(budgets.Reply, budgets.Session - session.Cost));
        return new RunOptions { Context = context, MemoryScope = memoryScope, Budget = new Budget { Cost = cost } };
    }

    /// <summary>Whether what is left of the session's budget, rather than the reply's own, limits the next reply.</summary>
    public bool SessionBudgetLimits(Session session) => budgets.Session - session.Cost <= budgets.Reply;

    /// <summary>
    /// Saves the session with its totals so far; returns whether it was saved. A failure is told once per reply
    /// (<paramref name="told"/>) and the reply goes on: the next save stores everything.
    /// </summary>
    public async Task<bool> SaveAsync(Session session, Usage usage, decimal cost, bool told)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            session.Saved = await store.SaveAsync(session.Conversation, session.StaffMember, usage, cost, session.Saved, CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
        {
            LogSaveFailed(logger, session.Id, exception.Message);
            if (!told)
            {
                await tell(exception is InvalidOperationException
                    ? $"[The session could not be saved: {exception.Message} Type /resume {session.Id} to go on from what was saved.]"
                    : $"[The session could not be saved: {exception.Message}]");
            }

            return false;
        }
    }

    /// <summary>
    /// Summarizes a session's conversation and stores the summary, adding its cost to the session's; returns it, or null
    /// (and says so) on failure.
    /// </summary>
    private async Task<SessionSummary?> SummarizeAsync(string id, Conversation conversation)
    {
        var result = await summarizer!.RunAsync(SessionSummarizer.Transcript(conversation), SessionSummarizer.Options);
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
            await tell($"[Session {id} could not be summarized: {reason}]");
            return null;
        }

        try
        {
            await store.SaveSummaryAsync(id, summary, result.Usage, result.Cost, CancellationToken.None);
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException)
        {
            LogSummaryFailed(logger, id, exception.Message);
            await tell($"[The summary of session {id} could not be saved: {exception.Message}]");
        }

        return summary;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {Session} could not be summarized: {Error}")]
    private static partial void LogSummaryFailed(ILogger logger, string session, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {Session} could not be saved: {Error}")]
    private static partial void LogSaveFailed(ILogger logger, string session, string error);
}
