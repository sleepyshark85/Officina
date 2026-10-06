using System.Text.Json;
using Npgsql;
using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>A stored session: its conversation, who started it, what its replies used, and the text it was stored as.</summary>
public sealed record StoredSession(Conversation Conversation, string StaffMember, Usage Usage, decimal Cost, string Saved);

/// <summary>A session as <c>/sessions</c> lists it; <paramref name="Stale"/> when it has no summary, or changed since.</summary>
public sealed record SessionListing(
    string Id, string StaffMember, string? Title, string? Summary, IReadOnlyList<string> Changes, decimal Cost, DateTimeOffset Updated, bool Stale);

/// <summary>
/// The session store: the <c>sessions</c> table, one row per conversation, keyed by its id. The conversation is kept as
/// the core's JSON in a text column, so it reads back byte for byte and resumes with its prefix and cache intact.
/// </summary>
public sealed class SessionStore(NpgsqlDataSource database)
{
    private const string CreateSql = """
        insert into sessions (id, staff_member, conversation, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost, updated)
        values ($1, $2, $3, $4, $5, $6, $7, $8, now())
        """;

    private const string SaveSql = """
        update sessions
        set conversation = $3, input_tokens = $4, output_tokens = $5, cache_read_tokens = $6, cache_write_tokens = $7, cost = $8,
            updated = case when conversation is distinct from $3 then now() else updated end
        where id = $1 and staff_member = $2 and conversation = $9
        """;

    private const string LoadSql = """
        select conversation, staff_member, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost
        from sessions
        where id = $1
        """;

    private const string ListSql = """
        select id, staff_member, title, summary, coalesce(changes, '{}'), cost, updated, summarized is null or summarized < updated
        from sessions
        order by updated desc
        limit $1
        """;

    private const string SummarySql = """
        update sessions
        set title = $2, summary = $3, changes = $4, summarized = now(), input_tokens = input_tokens + $5,
            output_tokens = output_tokens + $6, cache_read_tokens = cache_read_tokens + $7, cache_write_tokens = cache_write_tokens + $8,
            cost = cost + $9
        where id = $1
        """;

    /// <summary>
    /// Saves the session as it is now, and returns the text it is stored as. <paramref name="usage"/> and
    /// <paramref name="cost"/> replace the stored totals, so a failed or missing save loses nothing for good. Without
    /// <paramref name="previous"/> the session is inserted, and an id already taken throws rather than overwriting. With
    /// it, the session is updated only if it is still stored as <paramref name="previous"/>: one that another console
    /// changed since throws, rather than losing that console's messages. The database stamps the time, moving it only when
    /// the conversation changed, so a summary stays current through a save of the totals alone.
    /// </summary>
    public async Task<string> SaveAsync(
        Conversation conversation, string staffMember, Usage usage, decimal cost, string? previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var saved = JsonSerializer.Serialize(conversation);
        await using var command = database.CreateCommand(previous is null ? CreateSql : SaveSql);
        object[] values =
        [
            conversation.Id, staffMember, saved, usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite, cost, .. previous is null ? [] : new object[] { previous },
        ];
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException(
                $"session {conversation.Id} changed elsewhere since this console last saved it, so it was not overwritten. Type /resume {conversation.Id} to go on from what was saved.");
        }

        return saved;
    }

    /// <summary>
    /// The session with <paramref name="id"/>, or null when there is none. Throws <see cref="InvalidDataException"/> when its
    /// conversation cannot be read.
    /// </summary>
    public async Task<StoredSession?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(LoadSql);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var saved = reader.GetString(0);
        Conversation conversation;
        try
        {
            conversation = JsonSerializer.Deserialize<Conversation>(saved) ?? throw new JsonException("The conversation is null.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"Session {id} cannot be read: {exception.Message}", exception);
        }

        return new StoredSession(
            conversation, reader.GetString(1), new Usage(reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)),
            reader.GetDecimal(6), saved);
    }

    /// <summary>The <paramref name="count"/> sessions updated last, most recent first.</summary>
    public async Task<IReadOnlyList<SessionListing>> ListAsync(int count, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(ListSql);
        command.Parameters.Add(new NpgsqlParameter { Value = count });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<SessionListing>();
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new SessionListing(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<string[]>(4), reader.GetDecimal(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetBoolean(7)));
        }

        return sessions;
    }

    /// <summary>Stores the summary of session <paramref name="id"/>, as of now, and adds what it cost to the session's totals.</summary>
    public async Task SaveSummaryAsync(string id, SessionSummary summary, Usage usage, decimal cost, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);
        await using var command = database.CreateCommand(SummarySql);
        foreach (var value in new object[] { id, summary.Title, summary.Summary, summary.Changes.ToArray(), usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite, cost })
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
