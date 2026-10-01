using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>The in-memory storage (STO-01), used by the test kit. It passes the same contract tests as the SQLite storage.</summary>
public sealed class InMemoryStorage : IStorage
{
    public InMemoryRunStore Runs { get; } = new();

    public InMemoryEventLog Events { get; } = new();

    public InMemoryAuditLog Audit { get; } = new();

    IRunStore IStorage.Runs => Runs;

    IEventLog IStorage.Events => Events;

    IAuditLog IStorage.Audit => Audit;

    public ValueTask<OwnerData> ExportAsync(string? tenant, string owner, CancellationToken ct)
    {
        var runs = Runs.Rows.Where(tenant, run => run.Owner == owner);
        var ids = runs.Select(run => run.RunId).ToHashSet();
        return ValueTask.FromResult(new OwnerData(
            runs, Events.Rows.Where(tenant, coreEvent => ids.Contains(coreEvent.RunId)), Audit.Rows.Where(tenant, entry => ids.Contains(entry.RunId))));
    }

    public ValueTask DeleteAsync(string? tenant, string owner, CancellationToken ct)
    {
        var ids = Runs.Rows.Where(tenant, run => run.Owner == owner).Select(run => run.RunId).ToHashSet();
        Events.Rows.RemoveAll((rowTenant, coreEvent) => rowTenant == tenant && ids.Contains(coreEvent.RunId));
        Runs.Rows.RemoveAll((rowTenant, run) => rowTenant == tenant && ids.Contains(run.RunId));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteExpiredAsync(RetentionOptions retention, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retention);
        Runs.Rows.RemoveAll((_, run) => now - run.Time > retention.Runs);
        Events.Rows.RemoveAll((_, coreEvent) => now - coreEvent.Time > retention.Events);
        Audit.Rows.RemoveAll((_, entry) => now - entry.Time > retention.Audit);
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
