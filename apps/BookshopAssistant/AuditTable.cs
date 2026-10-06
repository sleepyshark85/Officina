using Npgsql;
using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>
/// The app's audit sink: each entry becomes a row of the <c>audit</c> table, written before the call returns; a failure
/// throws. It also reads a session's entries back for <c>/audit</c>: a session's id is its conversation's.
/// </summary>
public sealed class AuditTable(NpgsqlDataSource database) : IAuditSink
{
    private const string InsertSql = """
        insert into audit (time, sequence, run, conversation, agent, memory_scope, trace_id, span_id, kind, tool, call_id, input,
                           outcome, detail, duration, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost)
        values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20)
        """;

    private const string SelectSql = """
        select time, sequence, run, conversation, agent, memory_scope, trace_id, span_id, kind, tool, call_id, input,
               outcome, detail, duration, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost
        from audit
        where conversation = $1
        order by id
        """;

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var command = database.CreateCommand(InsertSql);
        object?[] values =
        [
            entry.Time, entry.Sequence, entry.Run, entry.Conversation, entry.Agent, entry.MemoryScope, entry.TraceId, entry.SpanId,
            entry.Kind.ToString(), entry.Tool, entry.CallId, entry.Input, entry.Outcome, entry.Detail, entry.Duration,
            entry.Usage?.Input, entry.Usage?.Output, entry.Usage?.CacheRead, entry.Usage?.CacheWrite, entry.Cost,
        ];
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The entries of <paramref name="conversation"/>, in the order they were written.</summary>
    public async Task<IReadOnlyList<AuditEntry>> ReadAsync(string conversation, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(SelectSql);
        command.Parameters.Add(new NpgsqlParameter { Value = conversation });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<AuditEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            T? Get<T>(int column) => reader.IsDBNull(column) ? default : reader.GetFieldValue<T>(column);
            entries.Add(new AuditEntry
            {
                Time = reader.GetFieldValue<DateTimeOffset>(0),
                Sequence = reader.GetInt64(1),
                Run = reader.GetString(2),
                Conversation = reader.GetString(3),
                Agent = reader.GetString(4),
                MemoryScope = Get<string>(5),
                TraceId = Get<string>(6),
                SpanId = Get<string>(7),
                Kind = Enum.Parse<AuditKind>(reader.GetString(8)),
                Tool = Get<string>(9),
                CallId = Get<string>(10),
                Input = Get<string>(11),
                Outcome = Get<string>(12),
                Detail = Get<string>(13),
                Duration = reader.IsDBNull(14) ? null : reader.GetFieldValue<TimeSpan>(14),
                Usage = reader.IsDBNull(15) ? null : new Usage(reader.GetInt64(15), reader.GetInt64(16), reader.GetInt64(17), reader.GetInt64(18)),
                Cost = Get<decimal?>(19),
            });
        }

        return entries;
    }
}
