namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// The append-only audit log (TOOL-11, INV-05). An append returns once the entry is durable, so an intent is recorded
/// before its tool runs (REL-03). Storage (S08) adds the durable implementation.
/// </summary>
public interface IAuditLog
{
    ValueTask AppendAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>A run's entries, in the order they were appended.</summary>
    ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(string runId, CancellationToken ct);
}
