namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>A file, report or data that a result carries as well as its text (OUT-05).</summary>
/// <param name="Name">What it is, such as <c>report.md</c>.</param>
/// <param name="Content">Its text.</param>
public sealed record Artifact(string Name, string Content);

/// <summary>
/// Where a run's artifacts are kept, such as the full text of a trimmed tool result, which the agent pages through
/// (TOOL-09). Artifacts are visible only within their tenant (SEC-02).
/// </summary>
public interface IArtifactStore
{
    /// <summary>Keeps an artifact of a run.</summary>
    /// <returns>Its id, unique in the storage.</returns>
    ValueTask<long> SaveAsync(string? tenant, string runId, Artifact artifact, DateTimeOffset time, CancellationToken ct);

    /// <summary>A run's artifact, or null if there is none with that id.</summary>
    ValueTask<Artifact?> ReadAsync(string? tenant, string runId, long id, CancellationToken ct);
}
