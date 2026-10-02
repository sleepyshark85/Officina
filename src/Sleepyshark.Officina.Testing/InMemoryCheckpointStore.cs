using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>Keeps checkpoints in memory, for tests.</summary>
public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    internal TenantRows<Checkpoint> Rows { get; } = new();

    public ValueTask AppendAsync(string? tenant, Checkpoint checkpoint, CancellationToken ct)
    {
        Rows.Add(tenant, checkpoint);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<Checkpoint>> ReadAsync(string? tenant, string runId, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<Checkpoint>>([.. Rows.Where(tenant, checkpoint => checkpoint.RunId == runId).OrderBy(checkpoint => checkpoint.Number)]);

    public ValueTask TruncateAsync(string? tenant, string runId, int number, CancellationToken ct)
    {
        Rows.RemoveAll((rowTenant, checkpoint) => rowTenant == tenant && checkpoint.RunId == runId && checkpoint.Number > number);
        return ValueTask.CompletedTask;
    }
}
