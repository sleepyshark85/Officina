using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>Keeps artifacts in memory, for tests.</summary>
public sealed class InMemoryArtifactStore : IArtifactStore
{
    private long last;

    internal TenantRows<(string RunId, long Id, DateTimeOffset Time, Artifact Artifact)> Rows { get; } = new();

    public ValueTask<long> SaveAsync(string? tenant, string runId, Artifact artifact, DateTimeOffset time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var id = Interlocked.Increment(ref last);
        Rows.Add(tenant, (runId, id, time, artifact));
        return ValueTask.FromResult(id);
    }

    public ValueTask<Artifact?> ReadAsync(string? tenant, string runId, long id, CancellationToken ct) =>
        ValueTask.FromResult(Rows.Where(tenant, row => row.RunId == runId && row.Id == id).Select(row => row.Artifact).FirstOrDefault());
}
