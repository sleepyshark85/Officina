using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>The in-memory storage (STO-01), used by the test kit. It passes the same contract tests as the SQLite storage.</summary>
public sealed class InMemoryStorage : IStorage
{
    public InMemoryRunStore Runs { get; } = new();

    public InMemoryConversationStore Conversations { get; } = new();

    public InMemoryEventLog Events { get; } = new();

    public InMemoryAuditLog Audit { get; } = new();

    public InMemoryRecordStore Records { get; } = new();

    public InMemoryArtifactStore Artifacts { get; } = new();

    public InMemoryTaskStore Tasks { get; } = new();

    IRunStore IStorage.Runs => Runs;

    IConversationStore IStorage.Conversations => Conversations;

    IEventLog IStorage.Events => Events;

    IAuditLog IStorage.Audit => Audit;

    IRecordStore IStorage.Records => Records;

    IArtifactStore IStorage.Artifacts => Artifacts;

    ITaskStore IStorage.Tasks => Tasks;

    public ValueTask<OwnerData> ExportAsync(string? tenant, string owner, CancellationToken ct)
    {
        var runs = Runs.Rows.Where(tenant, run => run.Owner == owner);
        var ids = runs.Select(run => run.RunId).ToHashSet();
        return ValueTask.FromResult(new OwnerData(
            runs, Events.Rows.Where(tenant, coreEvent => ids.Contains(coreEvent.RunId)), Audit.Rows.Where(tenant, entry => ids.Contains(entry.RunId)),
            Conversations.Rows.Where(tenant, turn => turn.Owner == owner), Records.Rows.Where(tenant, entry => ids.Contains(entry.RunId)),
            [.. Artifacts.Rows.Where(tenant, row => ids.Contains(row.RunId)).Select(row => row.Artifact)], Tasks.Rows.Where(tenant, change => ids.Contains(change.RunId))));
    }

    public ValueTask DeleteAsync(string? tenant, string owner, CancellationToken ct)
    {
        var ids = Runs.Rows.Where(tenant, run => run.Owner == owner).Select(run => run.RunId).ToHashSet();
        Events.Rows.RemoveAll((rowTenant, coreEvent) => rowTenant == tenant && ids.Contains(coreEvent.RunId));
        Records.Rows.RemoveAll((rowTenant, entry) => rowTenant == tenant && ids.Contains(entry.RunId));
        Artifacts.Rows.RemoveAll((rowTenant, row) => rowTenant == tenant && ids.Contains(row.RunId));
        Tasks.Rows.RemoveAll((rowTenant, change) => rowTenant == tenant && ids.Contains(change.RunId));
        Runs.Rows.RemoveAll((rowTenant, run) => rowTenant == tenant && ids.Contains(run.RunId));
        Conversations.Rows.RemoveAll((rowTenant, turn) => rowTenant == tenant && turn.Owner == owner);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteExpiredAsync(RetentionOptions retention, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retention);
        Runs.Rows.RemoveAll((_, run) => now - run.Time > retention.Runs);
        Conversations.Rows.RemoveAll((_, turn) => now - turn.Time > retention.Conversations);
        Events.Rows.RemoveAll((_, coreEvent) => now - coreEvent.Time > retention.Events);
        Audit.Rows.RemoveAll((_, entry) => now - entry.Time > retention.Audit);
        Artifacts.Rows.RemoveAll((_, row) => now - row.Time > retention.Artifacts);

        // A record or a board is deleted whole, once its last change is older than the period, so it is never trimmed (REC-05).
        var changed = Records.Rows.All.GroupBy(entry => entry.RunId).ToDictionary(run => run.Key, run => run.Max(entry => entry.Time));
        Records.Rows.RemoveAll((_, entry) => now - changed[entry.RunId] > retention.RunRecords);
        var boards = Tasks.Rows.All.GroupBy(change => change.RunId).ToDictionary(run => run.Key, run => run.Max(change => change.Time));
        Tasks.Rows.RemoveAll((_, change) => now - boards[change.RunId] > retention.TaskBoards);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Keeps events in memory, for tests.</summary>
public sealed class InMemoryEventLog : IEventLog
{
    internal TenantRows<CoreEvent> Rows { get; } = new();

    public ValueTask AppendAsync(string? tenant, CoreEvent coreEvent, CancellationToken ct)
    {
        Rows.Add(tenant, coreEvent);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<CoreEvent>> ReadAsync(string? tenant, string runId, long after, CancellationToken ct) =>
        ValueTask.FromResult(Rows.Where(tenant, coreEvent => coreEvent.RunId == runId && coreEvent.Sequence > after));
}
