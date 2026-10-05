using System.Text.Json;
using Npgsql;
using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>A stored session: its conversation, who started it, and what its replies used.</summary>
public sealed record StoredSession(Conversation Conversation, string StaffMember, Usage Usage, decimal Cost);

/// <summary>A session as <c>/sessions</c> lists it.</summary>
public sealed record SessionListing(string Id, string StaffMember, string? Title, decimal Cost, DateTimeOffset Updated);

/// <summary>
/// The session store (APP-10, ARCHITECTURE §12.1): the <c>sessions</c> table, one row per conversation, keyed by the
/// conversation's id. The conversation is kept as the core's JSON in a text column, so it reads back byte for byte and
/// resumes with its prefix, and its cache, intact.
/// </summary>
public sealed class SessionStore(NpgsqlDataSource database)
{
    private const string SaveSql = """
        insert into sessions (id, staff_member, conversation, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost, updated)
        values ($1, $2, $3, $4, $5, $6, $7, $8, now())
        on conflict (id) do update set
            conversation = excluded.conversation,
            input_tokens = sessions.input_tokens + excluded.input_tokens,
            output_tokens = sessions.output_tokens + excluded.output_tokens,
            cache_read_tokens = sessions.cache_read_tokens + excluded.cache_read_tokens,
            cache_write_tokens = sessions.cache_write_tokens + excluded.cache_write_tokens,
            cost = sessions.cost + excluded.cost,
            updated = excluded.updated
        """;

    private const string LoadSql = """
        select conversation, staff_member, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost
        from sessions
        where id = $1
        """;

    private const string ListSql = """
        select id, staff_member, title, cost, updated
        from sessions
        order by updated desc
        limit $1
        """;

    /// <summary>
    /// Saves the conversation as it is now, and adds <paramref name="usage"/> and <paramref name="cost"/> to the session's
    /// totals; a session not yet stored is created for <paramref name="staffMember"/>. The database
    /// stamps the time.
    /// </summary>
    public async Task SaveAsync(
        Conversation conversation, string staffMember, Usage usage, decimal cost, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        await using var command = database.CreateCommand(SaveSql);
        object[] values =
        [
            conversation.Id, staffMember, JsonSerializer.Serialize(conversation), usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite, cost,
        ];
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
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
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetDecimal(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return sessions;
    }
}
