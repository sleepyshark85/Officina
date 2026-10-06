using System.Text.Json;
using Npgsql;
using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>A stored session: its conversation, who started it, and what its replies used.</summary>
public sealed record StoredSession(Conversation Conversation, string StaffMember, Usage Usage, decimal Cost);

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
        where id = $1 and staff_member = $2
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
        update sessions set title = $2, summary = $3, changes = $4, summarized = now()
        where id = $1
        """;

    /// <summary>
    /// Saves the session as it is now. <paramref name="usage"/> and <paramref name="cost"/> replace the stored totals, so a
    /// failed or missing save loses nothing for good. A <paramref name="created"/> session is updated; a new one is inserted,
    /// and an id already taken throws rather than overwriting. The database stamps the time, moving it only when the
    /// conversation changed, so a summary stays current through a save of the totals alone.
    /// </summary>
    public async Task SaveAsync(
        Conversation conversation, string staffMember, Usage usage, decimal cost, bool created, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        await using var command = database.CreateCommand(created ? SaveSql : CreateSql);
        object[] values =
        [
            conversation.Id, staffMember, JsonSerializer.Serialize(conversation), usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite, cost,
        ];
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidOperationException($"Session {conversation.Id} of {staffMember} is not stored.");
        }
    }

    /// <summary>The session with <paramref name="id"/>, or null when there is none.</summary>
    public async Task<StoredSession?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(LoadSql);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredSession(
            JsonSerializer.Deserialize<Conversation>(reader.GetString(0))!,
            reader.GetString(1),
            new Usage(reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)),
            reader.GetDecimal(6));
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

    /// <summary>Stores the summary of session <paramref name="id"/>, as of now.</summary>
    public async Task SaveSummaryAsync(string id, SessionSummary summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);
        await using var command = database.CreateCommand(SummarySql);
        foreach (var value in new object[] { id, summary.Title, summary.Summary, summary.Changes.ToArray() })
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
