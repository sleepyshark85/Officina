using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Testing;

/// <summary>Keeps task boards in memory, for tests.</summary>
public sealed class InMemoryTaskStore : ITaskStore
{
    internal TenantRows<TaskChange> Rows { get; } = new();

    public ValueTask<bool> TryAppendAsync(string? tenant, TaskChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ValueTask.FromResult(Rows.TryAdd(tenant, change, row => row.RunId == change.RunId && row.Revision == change.Revision));
    }

    public ValueTask<IReadOnlyList<TaskChange>> ReadAsync(string? tenant, string runId, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<TaskChange>>([.. Rows.Where(tenant, change => change.RunId == runId).OrderBy(change => change.Revision)]);

    public ValueTask TruncateAsync(string? tenant, string runId, long revision, CancellationToken ct)
    {
        Rows.RemoveAll((rowTenant, change) => rowTenant == tenant && change.RunId == runId && change.Revision > revision);
        return ValueTask.CompletedTask;
    }
}
