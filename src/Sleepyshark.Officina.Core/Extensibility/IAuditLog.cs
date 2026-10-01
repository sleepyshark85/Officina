namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// The append-only audit log (TOOL-11, INV-05). An append returns once the entry is durable, so an intent is recorded
/// before its tool runs (REL-03). Entries are visible only within their tenant (SEC-02).
/// </summary>
public interface IAuditLog
{
    ValueTask AppendAsync(string? tenant, AuditEntry entry, CancellationToken ct);

    /// <summary>A run's entries, in the order they were appended.</summary>
    ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(string? tenant, string runId, CancellationToken ct);
}
