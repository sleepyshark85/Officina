using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Storage.Sqlite;

/// <summary>
/// The default local storage (STO-01, DESIGN.md §8): one SQLite file in WAL mode. Every row carries its tenant, and
/// every statement filters by it (SEC-02). The file carries the format version of what it holds, and a file in any
/// other version is refused rather than misread (REL-04).
/// </summary>
public sealed class SqliteStorage : IStorage, IRunStore, IConversationStore, IEventLog, IAuditLog, IRecordStore, IArtifactStore
{
    /// <summary>The only format version this storage reads and writes.</summary>
    public const int FormatVersion = 2;

    private const string Schema = """
        CREATE TABLE runs (
            run_id TEXT PRIMARY KEY, tenant TEXT, owner TEXT, agent TEXT NOT NULL, time INTEGER NOT NULL,
            core_version TEXT NOT NULL, configuration TEXT NOT NULL);
        CREATE INDEX runs_owner ON runs (tenant, owner);
        CREATE TABLE events (
            tenant TEXT, run_id TEXT NOT NULL, agent TEXT NOT NULL, step TEXT, sequence INTEGER NOT NULL,
            time INTEGER NOT NULL, payload TEXT NOT NULL);
        CREATE INDEX events_run ON events (tenant, run_id, sequence);
        CREATE TABLE audit (
            position INTEGER PRIMARY KEY, tenant TEXT, run_id TEXT NOT NULL, agent TEXT NOT NULL, caller TEXT,
            tool TEXT NOT NULL, arguments TEXT NOT NULL, decided_by TEXT, outcome TEXT NOT NULL, time INTEGER NOT NULL,
            idempotency_key TEXT NOT NULL, detail TEXT);
        CREATE INDEX audit_run ON audit (tenant, run_id);
        CREATE TABLE conversations (
            position INTEGER PRIMARY KEY, tenant TEXT, owner TEXT, agent TEXT NOT NULL, time INTEGER NOT NULL,
            shortened INTEGER NOT NULL, messages TEXT NOT NULL);
        CREATE INDEX conversations_owner ON conversations (tenant, owner, agent);
        CREATE TABLE record (
            tenant TEXT, run_id TEXT NOT NULL, revision INTEGER NOT NULL, agent TEXT NOT NULL, time INTEGER NOT NULL, item TEXT NOT NULL,
            PRIMARY KEY (run_id, revision));
        CREATE TABLE artifacts (
            id INTEGER PRIMARY KEY, tenant TEXT, run_id TEXT NOT NULL, time INTEGER NOT NULL, name TEXT NOT NULL, content TEXT NOT NULL);
        CREATE INDEX artifacts_run ON artifacts (tenant, run_id);
        """;

    private const string Conversation = "tenant IS $tenant AND agent = $agent AND owner IS $owner";

    private const string OwnerRuns = "SELECT run_id FROM runs WHERE tenant IS $tenant AND owner = $owner";

    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string connectionString;

    private SqliteStorage(string connectionString) => this.connectionString = connectionString;

    IRunStore IStorage.Runs => this;

    IConversationStore IStorage.Conversations => this;

    IEventLog IStorage.Events => this;

    IAuditLog IStorage.Audit => this;

    IRecordStore IStorage.Records => this;

    IArtifactStore IStorage.Artifacts => this;

    /// <summary>Opens the storage in a file, creating it if it does not exist.</summary>
    /// <exception cref="InvalidDataException">The file holds data in another format version.</exception>
    public static async Task<SqliteStorage> OpenAsync(string path, CancellationToken ct)
    {
        // Without pooling, no connection keeps the file open after use, so it can be moved or deleted.
        var storage = new SqliteStorage(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await using var connection = await storage.ConnectAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, "PRAGMA user_version");
        var version = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        if (version == 0)
        {
            command.CommandText = $"BEGIN; {Schema} PRAGMA user_version = {FormatVersion}; COMMIT;";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        else if (version != FormatVersion)
        {
            throw new InvalidDataException(
                $"{path} holds data in format version {version}, and this core reads format version {FormatVersion} only.");
        }

        command.CommandText = "PRAGMA journal_mode = WAL";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return storage;
    }

    public ValueTask RecordStartAsync(string? tenant, RunStarted run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        return ExecuteAsync(
            "INSERT INTO runs VALUES ($run_id, $tenant, $owner, $agent, $time, $core_version, $configuration)", ct,
            ("$run_id", run.RunId), ("$tenant", tenant), ("$owner", run.Owner), ("$agent", run.Agent), ("$time", run.Time.UtcTicks),
            ("$core_version", run.CoreVersion), ("$configuration", JsonSerializer.Serialize(run.Configuration, ConfigurationJson.Options)));
    }

    public ValueTask AppendAsync(string? tenant, CoreEvent coreEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(coreEvent);
        return ExecuteAsync(
            "INSERT INTO events VALUES ($tenant, $run_id, $agent, $step, $sequence, $time, $payload)", ct,
            ("$tenant", tenant), ("$run_id", coreEvent.RunId), ("$agent", coreEvent.Agent), ("$step", coreEvent.Step),
            ("$sequence", coreEvent.Sequence), ("$time", coreEvent.Time.UtcTicks), ("$payload", JsonSerializer.Serialize(coreEvent.Payload, StoredJson)));
    }

    public async ValueTask<IReadOnlyList<CoreEvent>> ReadAsync(string? tenant, string runId, long after, CancellationToken ct) =>
        await QueryAsync(
            "SELECT * FROM events WHERE tenant IS $tenant AND run_id = $run_id AND sequence > $after ORDER BY sequence", ReadEvent, ct,
            ("$tenant", tenant), ("$run_id", runId), ("$after", after)).ConfigureAwait(false);

    public ValueTask AppendAsync(string? tenant, AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ExecuteAsync(
            """
            INSERT INTO audit (tenant, run_id, agent, caller, tool, arguments, decided_by, outcome, time, idempotency_key, detail)
            VALUES ($tenant, $run_id, $agent, $caller, $tool, $arguments, $decided_by, $outcome, $time, $idempotency_key, $detail)
            """, ct,
            ("$tenant", tenant), ("$run_id", entry.RunId), ("$agent", entry.Agent), ("$caller", entry.Caller), ("$tool", entry.Tool),
            ("$arguments", entry.Arguments), ("$decided_by", entry.DecidedBy), ("$outcome", entry.Outcome.ToString()),
            ("$time", entry.Time.UtcTicks), ("$idempotency_key", entry.IdempotencyKey), ("$detail", entry.Detail));
    }

    public async ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(string? tenant, string runId, CancellationToken ct) =>
        await QueryAsync(
            "SELECT * FROM audit WHERE tenant IS $tenant AND run_id = $run_id ORDER BY position", ReadAuditEntry, ct,
            ("$tenant", tenant), ("$run_id", runId)).ConfigureAwait(false);

    public async ValueTask<bool> TryAppendAsync(string? tenant, RecordEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // The key (run, revision) lets only the first of two updates with the same revision in (REC-04); the other
        // inserts nothing, so it returns no row. Any other constraint violation still throws.
        var added = await QueryAsync(
            "INSERT INTO record VALUES ($tenant, $run_id, $revision, $agent, $time, $item) ON CONFLICT (run_id, revision) DO NOTHING RETURNING revision",
            row => row.GetInt64(0), ct,
            ("$tenant", tenant), ("$run_id", entry.RunId), ("$revision", entry.Revision), ("$agent", entry.Agent), ("$time", entry.Time.UtcTicks),
            ("$item", JsonSerializer.Serialize(entry.Item, StoredJson))).ConfigureAwait(false);
        return added.Count == 1;
    }

    async ValueTask<IReadOnlyList<RecordEntry>> IRecordStore.ReadAsync(string? tenant, string runId, CancellationToken ct) =>
        await QueryAsync(
            "SELECT * FROM record WHERE tenant IS $tenant AND run_id = $run_id ORDER BY revision", ReadRecordEntry, ct,
            ("$tenant", tenant), ("$run_id", runId)).ConfigureAwait(false);

    public async ValueTask<long> SaveAsync(string? tenant, string runId, Artifact artifact, DateTimeOffset time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var ids = await QueryAsync(
            "INSERT INTO artifacts (tenant, run_id, time, name, content) VALUES ($tenant, $run_id, $time, $name, $content) RETURNING id",
            row => row.GetInt64(0), ct,
            ("$tenant", tenant), ("$run_id", runId), ("$time", time.UtcTicks), ("$name", artifact.Name), ("$content", artifact.Content)).ConfigureAwait(false);
        return ids[0];
    }

    async ValueTask<Artifact?> IArtifactStore.ReadAsync(string? tenant, string runId, long id, CancellationToken ct) =>
        (await QueryAsync(
            "SELECT * FROM artifacts WHERE tenant IS $tenant AND run_id = $run_id AND id = $id", ReadArtifact, ct,
            ("$tenant", tenant), ("$run_id", runId), ("$id", id)).ConfigureAwait(false)).FirstOrDefault();

    public async ValueTask<OwnerData> ExportAsync(string? tenant, string owner, CancellationToken ct)
    {
        (string, object?)[] parameters = [("$tenant", tenant), ("$owner", owner)];
        var runs = await QueryAsync("SELECT * FROM runs WHERE tenant IS $tenant AND owner = $owner ORDER BY time", ReadRun, ct, parameters)
            .ConfigureAwait(false);
        var events = await QueryAsync(
            $"SELECT * FROM events WHERE tenant IS $tenant AND run_id IN ({OwnerRuns}) ORDER BY sequence", ReadEvent, ct, parameters).ConfigureAwait(false);
        var audit = await QueryAsync(
            $"SELECT * FROM audit WHERE tenant IS $tenant AND run_id IN ({OwnerRuns}) ORDER BY position", ReadAuditEntry, ct, parameters).ConfigureAwait(false);
        var conversations = await QueryAsync(
            "SELECT * FROM conversations WHERE tenant IS $tenant AND owner = $owner ORDER BY position", ReadTurn, ct, parameters).ConfigureAwait(false);
        var record = await QueryAsync(
            $"SELECT * FROM record WHERE tenant IS $tenant AND run_id IN ({OwnerRuns}) ORDER BY run_id, revision", ReadRecordEntry, ct, parameters).ConfigureAwait(false);
        var artifacts = await QueryAsync(
            $"SELECT * FROM artifacts WHERE tenant IS $tenant AND run_id IN ({OwnerRuns}) ORDER BY id", ReadArtifact, ct, parameters).ConfigureAwait(false);
        return new OwnerData(runs, events, audit, conversations, record, artifacts);
    }

    public ValueTask AppendAsync(string? tenant, ConversationTurn turn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return ExecuteAsync(
            "INSERT INTO conversations (tenant, owner, agent, time, shortened, messages) VALUES ($tenant, $owner, $agent, $time, $shortened, $messages)", ct,
            ("$tenant", tenant), ("$owner", turn.Owner), ("$agent", turn.Agent), ("$time", turn.Time.UtcTicks), ("$shortened", turn.Shortened),
            ("$messages", JsonSerializer.Serialize(turn.Messages, StoredJson)));
    }

    public async ValueTask<IReadOnlyList<ConversationTurn>> ReadAsync(string? tenant, string agent, string? owner, CancellationToken ct) =>
        await QueryAsync(
            $"""
            SELECT * FROM conversations WHERE {Conversation}
            AND position >= (SELECT COALESCE(MAX(position), 0) FROM conversations WHERE {Conversation} AND shortened) ORDER BY position
            """, ReadTurn, ct, ("$tenant", tenant), ("$agent", agent), ("$owner", owner)).ConfigureAwait(false);

    public ValueTask DeleteAsync(string? tenant, string owner, CancellationToken ct) =>
        ExecuteAsync(
            $"""
            DELETE FROM events WHERE tenant IS $tenant AND run_id IN ({OwnerRuns});
            DELETE FROM record WHERE tenant IS $tenant AND run_id IN ({OwnerRuns});
            DELETE FROM artifacts WHERE tenant IS $tenant AND run_id IN ({OwnerRuns});
            DELETE FROM runs WHERE tenant IS $tenant AND owner = $owner;
            DELETE FROM conversations WHERE tenant IS $tenant AND owner = $owner;
            """, ct,
            ("$tenant", tenant), ("$owner", owner));

    public ValueTask DeleteExpiredAsync(RetentionOptions retention, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retention);

        // A kind without a retention period is kept, so it needs no statement. Audit always has a period, so the
        // statement is never empty. A record is deleted whole, once its last change is older than its period, so it is
        // never trimmed (REC-05).
        (string Table, TimeSpan? Period, string Statement)[] kinds =
        [
            ("runs", retention.Runs, "DELETE FROM runs WHERE time < $runs;"),
            ("conversations", retention.Conversations, "DELETE FROM conversations WHERE time < $conversations;"),
            ("events", retention.Events, "DELETE FROM events WHERE time < $events;"),
            ("audit", retention.Audit, "DELETE FROM audit WHERE time < $audit;"),
            ("artifacts", retention.Artifacts, "DELETE FROM artifacts WHERE time < $artifacts;"),
            ("record", retention.RunRecords, "DELETE FROM record WHERE run_id IN (SELECT run_id FROM record GROUP BY run_id HAVING MAX(time) < $record);"),
        ];
        var expired = kinds.Where(kind => kind.Period is not null).ToList();

        // Subtracting ticks, not dates, so a very long period cannot go below the earliest date.
        return ExecuteAsync(
            string.Concat(expired.Select(kind => kind.Statement)), ct,
            [.. expired.Select(kind => ($"${kind.Table}", (object?)(now.UtcTicks - kind.Period!.Value.Ticks)))]);
    }

    private static RunStarted ReadRun(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("run_id")), row.GetString(row.GetOrdinal("agent")), Text(row, "owner"), Time(row),
        row.GetString(row.GetOrdinal("core_version")),
        JsonSerializer.Deserialize<OfficinaOptions>(row.GetString(row.GetOrdinal("configuration")), ConfigurationJson.Options)!);

    private static CoreEvent ReadEvent(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("run_id")), row.GetString(row.GetOrdinal("agent")), Text(row, "step"),
        row.GetInt64(row.GetOrdinal("sequence")), Time(row),
        JsonSerializer.Deserialize<EventPayload>(row.GetString(row.GetOrdinal("payload")), StoredJson)!);

    private static ConversationTurn ReadTurn(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("agent")), Text(row, "owner"), Time(row),
        JsonSerializer.Deserialize<ImmutableArray<Message>>(row.GetString(row.GetOrdinal("messages")), StoredJson), row.GetBoolean(row.GetOrdinal("shortened")));

    private static AuditEntry ReadAuditEntry(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("run_id")), row.GetString(row.GetOrdinal("agent")), Text(row, "caller"), row.GetString(row.GetOrdinal("tool")),
        row.GetString(row.GetOrdinal("arguments")), Text(row, "decided_by"), Enum.Parse<AuditOutcome>(row.GetString(row.GetOrdinal("outcome"))),
        Time(row), row.GetString(row.GetOrdinal("idempotency_key")), Text(row, "detail"));

    private static RecordEntry ReadRecordEntry(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("run_id")), row.GetInt64(row.GetOrdinal("revision")), row.GetString(row.GetOrdinal("agent")), Time(row),
        JsonSerializer.Deserialize<RecordItem>(row.GetString(row.GetOrdinal("item")), StoredJson)!);

    private static Artifact ReadArtifact(SqliteDataReader row) => new(row.GetString(row.GetOrdinal("name")), row.GetString(row.GetOrdinal("content")));

    private static string? Text(SqliteDataReader row, string column) =>
        row.IsDBNull(row.GetOrdinal(column)) ? null : row.GetString(row.GetOrdinal(column));

    private static DateTimeOffset Time(SqliteDataReader row) => new(row.GetInt64(row.GetOrdinal("time")), TimeSpan.Zero);

    private async ValueTask ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await ConnectAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> read, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var connection = await ConnectAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private async Task<SqliteConnection> ConnectAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
