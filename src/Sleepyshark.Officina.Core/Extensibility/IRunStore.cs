using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>Where runs are stored, each with the configuration it used (CFG-07) and its status (RUN-01).</summary>
public interface IRunStore
{
    /// <param name="tenant">The owner's tenant, or null; the run is visible only within it (SEC-02).</param>
    /// <param name="run">How the run started. Its status is <see cref="RunStatus.Running"/>.</param>
    /// <param name="ct">Cancels the write.</param>
    ValueTask RecordStartAsync(string? tenant, RunStarted run, CancellationToken ct);

    /// <summary>
    /// Records the run's status when it ends, or when a rollback makes it run again. A run that never gets this, because the
    /// process died, is still running as far as the store knows (RUN-04).
    /// </summary>
    ValueTask RecordStatusAsync(string? tenant, string runId, RunStatus status, CancellationToken ct);

    /// <summary>A run as stored; null when the tenant has no such run.</summary>
    ValueTask<StoredRun?> ReadAsync(string? tenant, string runId, CancellationToken ct);
}

/// <summary>A run as stored: how it started, and its status.</summary>
public sealed record StoredRun(RunStarted Started, RunStatus Status);
