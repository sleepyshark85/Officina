using Sleepyshark.Officina.Core.Checkpoints;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>Where a run's checkpoints are kept (RUN-03). Checkpoints are visible only within their tenant (SEC-02).</summary>
public interface ICheckpointStore
{
    ValueTask AppendAsync(string? tenant, Checkpoint checkpoint, CancellationToken ct);

    /// <summary>A run's checkpoints in order.</summary>
    ValueTask<IReadOnlyList<Checkpoint>> ReadAsync(string? tenant, string runId, CancellationToken ct);

    /// <summary>Deletes the checkpoints after <paramref name="number"/>, which a rollback to it leaves without a state to return to.</summary>
    ValueTask TruncateAsync(string? tenant, string runId, int number, CancellationToken ct);
}
