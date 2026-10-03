using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Memory;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Storage.Sqlite;

/// <summary>
/// The default local storage (STO-01, DESIGN.md §8): one SQLite file in WAL mode, and a folder with a file for each
/// artifact. Every row carries its tenant, and every statement filters by it (SEC-02). The file carries the format version
/// of what it holds, and a file in any other version is refused rather than misread (REL-04).
/// </summary>
/// <remarks>
/// An artifact's row holds what is known about it (its tenant, run, time, name, size and hash), and its file, named by the
/// row's id, holds its text. The file is written whole (to a temporary file that is then renamed) inside the transaction that
/// adds the row, so a row is never committed without its file. A crash in between leaves a file without a row, which the
/// next open removes. Rows are deleted first and their files after, so a crash there leaves files without rows too.
/// </remarks>
public sealed class SqliteStorage : IStorage, IRunStore, IConversationStore, IEventLog, IAuditLog, IRecordStore, IArtifactStore, ITaskStore, IMemoryStore, ICheckpointStore
{
    /// <summary>The only format version this storage reads and writes.</summary>
    public const int FormatVersion = 6;

    private const string Schema = """
        CREATE TABLE runs (
            run_id TEXT PRIMARY KEY, tenant TEXT, owner TEXT, agent TEXT NOT NULL, time INTEGER NOT NULL,
            core_version TEXT NOT NULL, configuration TEXT NOT NULL, input TEXT NOT NULL, trigger TEXT NOT NULL, task_id TEXT, status TEXT NOT NULL);
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
            shortened INTEGER NOT NULL, messages TEXT NOT NULL, prefix_memory INTEGER NOT NULL, seen_memory INTEGER NOT NULL, run_id TEXT);
        CREATE INDEX conversations_owner ON conversations (tenant, owner, agent);
        CREATE TABLE record (
            tenant TEXT, run_id TEXT NOT NULL, revision INTEGER NOT NULL, agent TEXT NOT NULL, time INTEGER NOT NULL, item TEXT NOT NULL, task TEXT,
            PRIMARY KEY (run_id, revision));
        CREATE TABLE artifacts (
            id INTEGER PRIMARY KEY AUTOINCREMENT, tenant TEXT, run_id TEXT NOT NULL, time INTEGER NOT NULL, name TEXT NOT NULL,
            size INTEGER NOT NULL, sha256 TEXT NOT NULL);
        CREATE INDEX artifacts_run ON artifacts (tenant, run_id);
        CREATE TABLE task_changes (
            tenant TEXT, run_id TEXT NOT NULL, revision INTEGER NOT NULL, by TEXT NOT NULL, time INTEGER NOT NULL, what TEXT NOT NULL,
            reason TEXT NOT NULL, tasks TEXT NOT NULL, PRIMARY KEY (run_id, revision));
        CREATE TABLE memory_changes (
            tenant TEXT, scope TEXT NOT NULL, revision INTEGER NOT NULL, by TEXT NOT NULL, time INTEGER NOT NULL, action TEXT NOT NULL,
            proposal INTEGER NOT NULL, content TEXT, comment TEXT);
        CREATE UNIQUE INDEX memory_changes_scope ON memory_changes (COALESCE(tenant, ''), scope, revision);
        CREATE TABLE checkpoints (
            tenant TEXT, run_id TEXT NOT NULL, number INTEGER NOT NULL, time INTEGER NOT NULL, state TEXT NOT NULL, PRIMARY KEY (run_id, number));
        """;

    private const string Conversation = "tenant IS $tenant AND agent = $agent AND owner IS $owner";

    private const string TemporaryExtension = ".tmp";

    private const string OwnerRuns ="SELECT run_id FROM runs WHERE tenant IS $tenant AND owner = $owner";

    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string connectionString;

    private readonly string artifacts;

    private SqliteStorage(string connectionString, string artifacts)
    {
        this.connectionString = connectionString;
        this.artifacts = artifacts;
    }

    IRunStore IStorage.Runs => this;

    IConversationStore IStorage.Conversations => this;

    IEventLog IStorage.Events => this;

    IAuditLog IStorage.Audit => this;

    IRecordStore IStorage.Records => this;

    IArtifactStore IStorage.Artifacts => this;

    ITaskStore IStorage.Tasks => this;

    IMemoryStore IStorage.Memory => this;

    ICheckpointStore IStorage.Checkpoints => this;

    /// <summary>Opens the storage in a file, creating it if it does not exist, with the artifacts' files in a folder <c>artifacts</c> beside it.</summary>
    /// <exception cref="InvalidDataException">The file holds data in another format version.</exception>
    public static Task<SqliteStorage> OpenAsync(string path, CancellationToken ct) => OpenAsync(path, ArtifactsBeside(path), ct);

    /// <summary>
    /// Opens the storage in a file, creating it if it does not exist, with the artifacts' files in the folder <paramref name="artifacts"/>,
    /// which is made when the first artifact is saved. Files a crash left there without a row are removed.
    /// </summary>
    /// <exception cref="InvalidDataException">The file holds data in another format version.</exception>
    public static async Task<SqliteStorage> OpenAsync(string path, string artifacts, CancellationToken ct)
    {
        // Without pooling, no connection keeps the file open after use, so it can be moved or deleted.
        var storage = new SqliteStorage(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString(), Path.GetFullPath(artifacts));
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
                $"{path} holds data in format version {version}, and this core reads format version {FormatVersion} only. " +
                "Move it aside, with its artifacts folder, to start with empty storage, or use the version that wrote it.");
        }

        command.CommandText = "PRAGMA journal_mode = WAL";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await storage.RemoveFilesWithoutRowsAsync(connection, ct).ConfigureAwait(false);
        return storage;
    }

    /// <summary>The folder <c>artifacts</c> beside a database file: where its artifacts' files are kept unless another is given.</summary>
    public static string ArtifactsBeside(string path) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "artifacts");

    public ValueTask RecordStartAsync(string? tenant, RunStarted run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        return ExecuteAsync(
            "INSERT INTO runs VALUES ($run_id, $tenant, $owner, $agent, $time, $core_version, $configuration, $input, $trigger, $task_id, $status)", ct,
            ("$run_id", run.RunId), ("$tenant", tenant), ("$owner", run.Owner), ("$agent", run.Agent), ("$time", run.Time.UtcTicks),
            ("$core_version", run.CoreVersion), ("$configuration", JsonSerializer.Serialize(run.Configuration, ConfigurationJson.Options)),
            ("$input", run.Input), ("$trigger", run.Trigger.ToString()), ("$task_id", run.TaskId), ("$status", RunStatus.Running.ToString()));
    }

    public ValueTask RecordStatusAsync(string? tenant, string runId, RunStatus status, CancellationToken ct) =>
        ExecuteAsync(
            "UPDATE runs SET status = $status WHERE tenant IS $tenant AND run_id = $run_id", ct,
            ("$status", status.ToString()), ("$tenant", tenant), ("$run_id", runId));

    async ValueTask<StoredRun?> IRunStore.ReadAsync(string? tenant, string runId, CancellationToken ct) =>
        (await QueryAsync(
            "SELECT * FROM runs WHERE tenant IS $tenant AND run_id = $run_id", row => new StoredRun(ReadRun(row), Enum.Parse<RunStatus>(row.GetString(row.GetOrdinal("status")))), ct,
            ("$tenant", tenant), ("$run_id", runId)).ConfigureAwait(false)).FirstOrDefault();

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
            "INSERT INTO record VALUES ($tenant, $run_id, $revision, $agent, $time, $item, $task) ON CONFLICT (run_id, revision) DO NOTHING RETURNING revision",
            row => row.GetInt64(0), ct,
            ("$tenant", tenant), ("$run_id", entry.RunId), ("$revision", entry.Revision), ("$agent", entry.Agent), ("$time", entry.Time.UtcTicks),
            ("$item", JsonSerializer.Serialize(entry.Item, StoredJson)), ("$task", entry.Task)).ConfigureAwait(false);
        return added.Count == 1;
    }

    async ValueTask<IReadOnlyList<RecordEntry>> IRecordStore.ReadAsync(string? tenant, string runId, CancellationToken ct) =>
        await QueryAsync(
            "SELECT * FROM record WHERE tenant IS $tenant AND run_id = $run_id ORDER BY revision", ReadRecordEntry, ct,
            ("$tenant", tenant), ("$run_id", runId)).ConfigureAwait(false);

    public ValueTask TruncateAsync(string? tenant, string runId, long revision, CancellationToken ct) =>
        ExecuteAsync(
            "DELETE FROM record WHERE tenant IS $tenant AND run_id = $run_id AND revision > $revision", ct,
            ("$tenant", tenant), ("$run_id", runId), ("$revision", revision));

    public async ValueTask<bool> TryAppendAsync(string? tenant, TaskChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);

        // As for the record: the key (run, revision) lets only the first of two changes with the same revision in.
        var added = await QueryAsync(
            "INSERT INTO task_changes VALUES ($tenant, $run_id, $revision, $by, $time, $what, $reason, $tasks) ON CONFLICT (run_id, revision) DO NOTHING RETURNING revision",
            row => row.GetInt64(0), ct,
            ("$tenant", tenant), ("$run_id", change.RunId), ("$revision", change.Revision), ("$by", change.By), ("$time", change.Time.UtcTicks),
            ("$what", change.What), ("$reason", change.Reason), ("$tasks", JsonSerializer.Serialize(change.Tasks, StoredJson))).ConfigureAwait(false);
        return added.Count == 1;
    }

    async ValueTask<IReadOnlyList<TaskChange>> ITaskStore.ReadAsync(string? tenant, string runId, CancellationToken ct) =>
        await QueryAsync(
            "SELECT * FROM task_changes WHERE tenant IS $tenant AND run_id = $run_id ORDER BY revision", ReadTaskChange, ct,
            ("$tenant", tenant), ("$run_id", runId)).ConfigureAwait(false);

    ValueTask ITaskStore.TruncateAsync(string? tenant, string runId, long revision, CancellationToken ct) =>
        ExecuteAsync(
            "DELETE FROM task_changes WHERE tenant IS $tenant AND run_id = $run_id AND revision > $revision", ct,
            ("$tenant", tenant), ("$run_id", runId), ("$revision", revision));

    async ValueTask<bool> IMemoryStore.TryAppendAsync(string? tenant, MemoryChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);

        // The unique index on (tenant, scope, revision) lets only the first of two changes with the same revision in.
        var added = await QueryAsync(
            "INSERT INTO memory_changes VALUES ($tenant, $scope, $revision, $by, $time, $action, $proposal, $content, $comment) ON CONFLICT DO NOTHING RETURNING revision",
            row => row.GetInt64(0), ct,
            ("$tenant", tenant), ("$scope", change.Scope), ("$revision", change.Revision), ("$by", change.By), ("$time", change.Time.UtcTicks),
            ("$action", change.Action.ToString()), ("$proposal", change.Proposal),
            ("$content", change.Content is null ? null : JsonSerializer.Serialize(change.Content, StoredJson)), ("$comment", change.Comment)).ConfigureAwait(false);
        return added.Count == 1;
    }

    async ValueTask<IReadOnlyList<MemoryChange>> IMemoryStore.ReadAsync(string? tenant, string scope, CancellationToken ct) =>
        await QueryAsync(
            "SELECT * FROM memory_changes WHERE tenant IS $tenant AND scope = $scope ORDER BY revision", ReadMemoryChange, ct,
            ("$tenant", tenant), ("$scope", scope)).ConfigureAwait(false);

    public ValueTask AppendAsync(string? tenant, Checkpoint checkpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return ExecuteAsync(
            "INSERT INTO checkpoints VALUES ($tenant, $run_id, $number, $time, $state)", ct,
            ("$tenant", tenant), ("$run_id", checkpoint.RunId), ("$number", checkpoint.Number), ("$time", checkpoint.Time.UtcTicks),
            ("$state", JsonSerializer.Serialize(checkpoint, StoredJson)));
    }

    async ValueTask<IReadOnlyList<Checkpoint>> ICheckpointStore.ReadAsync(string? tenant, string runId, CancellationToken ct) =>
        await QueryAsync(
            "SELECT * FROM checkpoints WHERE tenant IS $tenant AND run_id = $run_id ORDER BY number", ReadCheckpoint, ct,
            ("$tenant", tenant), ("$run_id", runId)).ConfigureAwait(false);

    public ValueTask TruncateAsync(string? tenant, string runId, int number, CancellationToken ct) =>
        ExecuteAsync(
            "DELETE FROM checkpoints WHERE tenant IS $tenant AND run_id = $run_id AND number > $number", ct,
            ("$tenant", tenant), ("$run_id", runId), ("$number", number));

    public async ValueTask<long> SaveAsync(string? tenant, string runId, Artifact artifact, DateTimeOffset time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var content = Encoding.UTF8.GetBytes(artifact.Content);

        // The transaction holds the write lock until the file is in place, so neither another save nor the removal of files
        // without rows comes in between, and the row is committed only once its file is there.
        await using var connection = await ConnectAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = Command(
            connection, "INSERT INTO artifacts (tenant, run_id, time, name, size, sha256) VALUES ($tenant, $run_id, $time, $name, $size, $sha256) RETURNING id",
            ("$tenant", tenant), ("$run_id", runId), ("$time", time.UtcTicks), ("$name", artifact.Name), ("$size", content.Length),
            ("$sha256", Convert.ToHexStringLower(SHA256.HashData(content))));
        var id = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        await WriteFileAsync(id, content, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return id;
    }

    async ValueTask<Artifact?> IArtifactStore.ReadAsync(string? tenant, string runId, long id, CancellationToken ct)
    {
        const string Sql = "SELECT * FROM artifacts WHERE tenant IS $tenant AND run_id = $run_id AND id = $id";
        (string, object?)[] parameters = [("$tenant", tenant), ("$run_id", runId), ("$id", id)];
        if ((await QueryAsync(Sql, ArtifactRow.Read, ct, parameters).ConfigureAwait(false)).FirstOrDefault() is not { } row)
        {
            return null;
        }

        try
        {
            return await ReadFileAsync(row, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted since its row was read; a file missing under a row that is still there is damage.
            return (await QueryAsync(Sql, ArtifactRow.Read, ct, parameters).ConfigureAwait(false)).Count == 0
                ? null
                : throw new InvalidDataException($"The file of artifact {id}, {FilePath(id)}, is missing.", exception);
        }
    }

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
        var artifacts = new List<Artifact>();
        foreach (var row in await QueryAsync(
            $"SELECT * FROM artifacts WHERE tenant IS $tenant AND run_id IN ({OwnerRuns}) ORDER BY id", ArtifactRow.Read, ct, parameters).ConfigureAwait(false))
        {
            artifacts.Add(await ReadFileAsync(row, ct).ConfigureAwait(false));
        }

        var tasks = await QueryAsync(
            $"SELECT * FROM task_changes WHERE tenant IS $tenant AND run_id IN ({OwnerRuns}) ORDER BY run_id, revision", ReadTaskChange, ct, parameters).ConfigureAwait(false);
        var memory = await QueryAsync(
            "SELECT * FROM memory_changes WHERE tenant IS $tenant AND scope = $scope ORDER BY revision", ReadMemoryChange, ct,
            ("$tenant", tenant), ("$scope", ProjectMemory.OwnerScope(owner))).ConfigureAwait(false);
        var checkpoints = await QueryAsync(
            $"SELECT * FROM checkpoints WHERE tenant IS $tenant AND run_id IN ({OwnerRuns}) ORDER BY run_id, number", ReadCheckpoint, ct, parameters).ConfigureAwait(false);
        return new OwnerData(runs, events, audit, conversations, record, artifacts, tasks, memory, checkpoints);
    }

    public ValueTask AppendAsync(string? tenant, ConversationTurn turn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turn);
        return ExecuteAsync(
            "INSERT INTO conversations (tenant, owner, agent, time, shortened, messages, prefix_memory, seen_memory, run_id) VALUES ($tenant, $owner, $agent, $time, $shortened, $messages, $prefix_memory, $seen_memory, $run_id)", ct,
            ("$tenant", tenant), ("$owner", turn.Owner), ("$agent", turn.Agent), ("$time", turn.Time.UtcTicks), ("$shortened", turn.Shortened),
            ("$messages", JsonSerializer.Serialize(turn.Messages, StoredJson)), ("$prefix_memory", turn.PrefixMemory), ("$seen_memory", turn.SeenMemory), ("$run_id", turn.RunId));
    }

    public async ValueTask<int> CountAsync(string? tenant, string agent, string? owner, CancellationToken ct) =>
        (await QueryAsync(
            $"SELECT COUNT(*) FROM conversations WHERE {Conversation}", row => row.GetInt32(0), ct, ("$tenant", tenant), ("$agent", agent), ("$owner", owner)).ConfigureAwait(false))[0];

    public async ValueTask<int> CountOtherRunsAfterAsync(string? tenant, string agent, string? owner, int count, string runId, CancellationToken ct) =>
        (await QueryAsync(
            $"""
            SELECT COUNT(*) FROM conversations WHERE {Conversation} AND run_id IS NOT $run_id
            AND position NOT IN (SELECT position FROM conversations WHERE {Conversation} ORDER BY position LIMIT $count)
            """, row => row.GetInt32(0), ct, ("$tenant", tenant), ("$agent", agent), ("$owner", owner), ("$run_id", runId), ("$count", count)).ConfigureAwait(false))[0];

    public ValueTask TruncateAsync(string? tenant, string agent, string? owner, int count, string runId, CancellationToken ct) =>
        ExecuteAsync(
            $"""
            DELETE FROM conversations WHERE {Conversation}
            AND position NOT IN (SELECT position FROM conversations WHERE {Conversation} ORDER BY position LIMIT $count)
            AND NOT EXISTS (
                SELECT 1 FROM conversations WHERE {Conversation} AND run_id IS NOT $run_id
                AND position NOT IN (SELECT position FROM conversations WHERE {Conversation} ORDER BY position LIMIT $count))
            """, ct, ("$tenant", tenant), ("$agent", agent), ("$owner", owner), ("$run_id", runId), ("$count", count));

    public async ValueTask<IReadOnlyList<ConversationTurn>> ReadAsync(string? tenant, string agent, string? owner, CancellationToken ct) =>
        await QueryAsync(
            $"""
            SELECT * FROM conversations WHERE {Conversation}
            AND position >= (SELECT COALESCE(MAX(position), 0) FROM conversations WHERE {Conversation} AND shortened) ORDER BY position
            """, ReadTurn, ct, ("$tenant", tenant), ("$agent", agent), ("$owner", owner)).ConfigureAwait(false);

    public ValueTask DeleteAsync(string? tenant, string owner, CancellationToken ct) =>
        DeleteWithFilesAsync(
            $"DELETE FROM artifacts WHERE tenant IS $tenant AND run_id IN ({OwnerRuns}) RETURNING id",
            $"""
            DELETE FROM events WHERE tenant IS $tenant AND run_id IN ({OwnerRuns});
            DELETE FROM record WHERE tenant IS $tenant AND run_id IN ({OwnerRuns});
            DELETE FROM task_changes WHERE tenant IS $tenant AND run_id IN ({OwnerRuns});
            DELETE FROM checkpoints WHERE tenant IS $tenant AND run_id IN ({OwnerRuns});
            DELETE FROM runs WHERE tenant IS $tenant AND owner = $owner;
            DELETE FROM conversations WHERE tenant IS $tenant AND owner = $owner;
            DELETE FROM memory_changes WHERE tenant IS $tenant AND scope = $scope;
            """, ct,
            ("$tenant", tenant), ("$owner", owner), ("$scope", ProjectMemory.OwnerScope(owner)));

    public ValueTask DeleteExpiredAsync(RetentionOptions retention, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retention);

        // A kind without a retention period is kept, so it needs no statement. Audit always has a period, so the
        // statement is never empty. A record is deleted whole, once its last change is older than its period, so it is
        // never trimmed (REC-05), and so is a task board.
        (string Table, TimeSpan? Period, string Statement)[] kinds =
        [
            ("runs", retention.Runs, "DELETE FROM runs WHERE time < $runs;"),
            ("conversations", retention.Conversations, "DELETE FROM conversations WHERE time < $conversations;"),
            ("events", retention.Events, "DELETE FROM events WHERE time < $events;"),
            ("audit", retention.Audit, "DELETE FROM audit WHERE time < $audit;"),
            ("record", retention.RunRecords, "DELETE FROM record WHERE run_id IN (SELECT run_id FROM record GROUP BY run_id HAVING MAX(time) < $record);"),
            ("tasks", retention.TaskBoards,
                "DELETE FROM task_changes WHERE run_id IN (SELECT run_id FROM task_changes GROUP BY run_id HAVING MAX(time) < $tasks);"),
            ("checkpoints", retention.RunRecords,
                "DELETE FROM checkpoints WHERE run_id IN (SELECT run_id FROM checkpoints GROUP BY run_id HAVING MAX(time) < $checkpoints);"),
        ];
        var expired = kinds.Where(kind => kind.Period is not null).ToList();

        // Subtracting ticks, not dates, so a very long period cannot go below the earliest date.
        (string, object?)[] parameters =
        [
            .. expired.Select(kind => ($"${kind.Table}", (object?)(now.UtcTicks - kind.Period!.Value.Ticks))),
            ("$artifacts", now.UtcTicks - (retention.Artifacts ?? TimeSpan.Zero).Ticks),
        ];
        return DeleteWithFilesAsync(
            retention.Artifacts is null ? null : "DELETE FROM artifacts WHERE time < $artifacts RETURNING id", string.Concat(expired.Select(kind => kind.Statement)), ct,
            parameters);
    }

    /// <summary>
    /// Deletes rows with <paramref name="sql"/>, and artifacts' rows with <paramref name="artifactsSql"/>, which returns their ids, in one
    /// transaction; then the artifacts' files.
    /// </summary>
    private async ValueTask DeleteWithFilesAsync(string? artifactsSql, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var deleted = new List<long>();
        await using (var connection = await ConnectAsync(ct).ConfigureAwait(false))
        {
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            if (artifactsSql is not null)
            {
                await using var artifactsCommand = Command(connection, artifactsSql, parameters);
                await using var reader = await artifactsCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    deleted.Add(reader.GetInt64(0));
                }
            }

            await using var command = Command(connection, sql, parameters);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        // After the commit, so a crash leaves files without rows, which the next open removes, never rows without files.
        foreach (var id in deleted)
        {
            DeleteFile(FilePath(id));
        }
    }

    private string FilePath(long id) => Path.Combine(artifacts, id.ToString(CultureInfo.InvariantCulture));

    /// <summary>Writes an artifact's file whole: to a temporary file, flushed to the disk, then renamed over any file a crash left.</summary>
    private async Task WriteFileAsync(long id, byte[] content, CancellationToken ct)
    {
        Directory.CreateDirectory(artifacts);
        var path = FilePath(id);
        var temporary = path + TemporaryExtension;
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await stream.WriteAsync(content, ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <exception cref="InvalidDataException">The file is not the one the row describes.</exception>
    private async Task<Artifact> ReadFileAsync(ArtifactRow row, CancellationToken ct)
    {
        var content = await File.ReadAllBytesAsync(FilePath(row.Id), ct).ConfigureAwait(false);
        return content.Length == row.Size && Convert.ToHexStringLower(SHA256.HashData(content)) == row.Sha256
            ? new Artifact(row.Name, Encoding.UTF8.GetString(content))
            : throw new InvalidDataException($"The file of artifact {row.Id}, {FilePath(row.Id)}, is damaged: it is not what was saved.");
    }

    /// <summary>
    /// Removes the files a crash left in the artifacts folder: those without a row, and temporary ones. It holds the write lock,
    /// so no artifact is being saved meanwhile. Only files named as this storage names them are touched.
    /// </summary>
    private async Task RemoveFilesWithoutRowsAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (!Directory.Exists(artifacts))
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = Command(connection, "SELECT id FROM artifacts");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetInt64(0).ToString(CultureInfo.InvariantCulture));
            }
        }

        foreach (var file in Directory.EnumerateFiles(artifacts))
        {
            var name = Path.GetFileName(file);
            var temporary = name.EndsWith(TemporaryExtension, StringComparison.Ordinal);
            var id = temporary ? name[..^TemporaryExtension.Length] : name;
            if (id.Length > 0 && id.All(char.IsAsciiDigit) && (temporary || !ids.Contains(id)))
            {
                DeleteFile(file);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a file; one that is open, which Windows does not delete, is left for the next open to remove.</summary>
    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>What an artifact's row holds, its file aside.</summary>
    private sealed record ArtifactRow(long Id, string Name, long Size, string Sha256)
    {
        public static ArtifactRow Read(SqliteDataReader row) => new(
            row.GetInt64(row.GetOrdinal("id")), row.GetString(row.GetOrdinal("name")), row.GetInt64(row.GetOrdinal("size")), row.GetString(row.GetOrdinal("sha256")));
    }

    private static RunStarted ReadRun(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("run_id")), row.GetString(row.GetOrdinal("agent")), Text(row, "owner"), Time(row),
        row.GetString(row.GetOrdinal("core_version")),
        JsonSerializer.Deserialize<OfficinaOptions>(row.GetString(row.GetOrdinal("configuration")), ConfigurationJson.Options)!)
    {
        Input = row.GetString(row.GetOrdinal("input")),
        Trigger = Enum.Parse<Trigger>(row.GetString(row.GetOrdinal("trigger"))),
        TaskId = Text(row, "task_id"),
    };

    private static Checkpoint ReadCheckpoint(SqliteDataReader row) => JsonSerializer.Deserialize<Checkpoint>(row.GetString(row.GetOrdinal("state")), StoredJson)!;

    private static CoreEvent ReadEvent(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("run_id")), row.GetString(row.GetOrdinal("agent")), Text(row, "step"),
        row.GetInt64(row.GetOrdinal("sequence")), Time(row),
        JsonSerializer.Deserialize<EventPayload>(row.GetString(row.GetOrdinal("payload")), StoredJson)!);

    private static ConversationTurn ReadTurn(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("agent")), Text(row, "owner"), Time(row),
        JsonSerializer.Deserialize<ImmutableArray<Message>>(row.GetString(row.GetOrdinal("messages")), StoredJson), row.GetBoolean(row.GetOrdinal("shortened")),
        row.GetInt64(row.GetOrdinal("prefix_memory")), row.GetInt64(row.GetOrdinal("seen_memory")), Text(row, "run_id"));

    private static AuditEntry ReadAuditEntry(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("run_id")), row.GetString(row.GetOrdinal("agent")), Text(row, "caller"), row.GetString(row.GetOrdinal("tool")),
        row.GetString(row.GetOrdinal("arguments")), Text(row, "decided_by"), Enum.Parse<AuditOutcome>(row.GetString(row.GetOrdinal("outcome"))),
        Time(row), row.GetString(row.GetOrdinal("idempotency_key")), Text(row, "detail"));

    private static RecordEntry ReadRecordEntry(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("run_id")), row.GetInt64(row.GetOrdinal("revision")), row.GetString(row.GetOrdinal("agent")), Time(row),
        JsonSerializer.Deserialize<RecordItem>(row.GetString(row.GetOrdinal("item")), StoredJson)!, Text(row, "task"));

    private static TaskChange ReadTaskChange(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("run_id")), row.GetInt64(row.GetOrdinal("revision")), row.GetString(row.GetOrdinal("by")), Time(row),
        row.GetString(row.GetOrdinal("what")), row.GetString(row.GetOrdinal("reason")),
        JsonSerializer.Deserialize<List<BoardTask>>(row.GetString(row.GetOrdinal("tasks")), StoredJson)!);

    private static MemoryChange ReadMemoryChange(SqliteDataReader row) => new(
        row.GetString(row.GetOrdinal("scope")), row.GetInt64(row.GetOrdinal("revision")), row.GetString(row.GetOrdinal("by")), Time(row),
        Enum.Parse<MemoryAction>(row.GetString(row.GetOrdinal("action"))), row.GetInt64(row.GetOrdinal("proposal")),
        Text(row, "content") is { } content ? JsonSerializer.Deserialize<MemoryProposal>(content, StoredJson) : null, Text(row, "comment"));

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
