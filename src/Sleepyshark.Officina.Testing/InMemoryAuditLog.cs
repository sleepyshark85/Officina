using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>Keeps the audit log in memory, for tests. Durable storage (S08) implements <see cref="IAuditLog"/> in its own project.</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly ConcurrentQueue<AuditEntry> entries = new();

    /// <summary>Every entry, in the order appended.</summary>
    public IReadOnlyList<AuditEntry> Entries => [.. entries];

    public ValueTask AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        entries.Enqueue(entry ?? throw new ArgumentNullException(nameof(entry)));
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(string runId, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<AuditEntry>>([.. entries.Where(entry => entry.RunId == runId)]);
}
