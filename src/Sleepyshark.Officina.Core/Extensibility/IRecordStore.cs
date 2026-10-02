using Sleepyshark.Officina.Core.Records;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>Where run records are kept (REC-01). Entries are visible only within their tenant (SEC-02).</summary>
public interface IRecordStore
{
    /// <summary>
    /// Appends an entry in one all-or-nothing write, unless the run's record already has an entry with its revision:
    /// then another update came first, nothing is written, and the result is false (REC-04, CONC-01).
    /// </summary>
    ValueTask<bool> TryAppendAsync(string? tenant, RecordEntry entry, CancellationToken ct);

    /// <summary>A run's record, in revision order.</summary>
    ValueTask<IReadOnlyList<RecordEntry>> ReadAsync(string? tenant, string runId, CancellationToken ct);

    /// <summary>Deletes the entries after a revision, when the run goes back to a checkpoint (RUN-08).</summary>
    ValueTask TruncateAsync(string? tenant, string runId, long revision, CancellationToken ct);
}
