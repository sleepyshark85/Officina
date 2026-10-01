using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>Keeps the audit log in memory, for tests.</summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    internal TenantRows<AuditEntry> Rows { get; } = new();

    /// <summary>Every entry, in every tenant, in the order appended.</summary>
    public IReadOnlyList<AuditEntry> Entries => Rows.All;

    public ValueTask AppendAsync(string? tenant, AuditEntry entry, CancellationToken ct)
    {
        Rows.Add(tenant, entry);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AuditEntry>> ReadAsync(string? tenant, string runId, CancellationToken ct) =>
        ValueTask.FromResult(Rows.Where(tenant, entry => entry.RunId == runId));
}
