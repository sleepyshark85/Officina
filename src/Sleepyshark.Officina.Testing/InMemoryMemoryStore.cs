using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Memory;

namespace Sleepyshark.Officina.Testing;

/// <summary>Keeps project memory in memory, for tests.</summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    internal TenantRows<MemoryChange> Rows { get; } = new();

    public ValueTask<bool> TryAppendAsync(string? tenant, MemoryChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ValueTask.FromResult(Rows.TryAdd(tenant, change, row => row.Scope == change.Scope && row.Revision == change.Revision));
    }

    public ValueTask<IReadOnlyList<MemoryChange>> ReadAsync(string? tenant, string scope, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<MemoryChange>>([.. Rows.Where(tenant, change => change.Scope == scope).OrderBy(change => change.Revision)]);
}
