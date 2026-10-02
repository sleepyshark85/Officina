using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Records;

namespace Sleepyshark.Officina.Testing;

/// <summary>Keeps run records in memory, for tests.</summary>
public sealed class InMemoryRecordStore : IRecordStore
{
    internal TenantRows<RecordEntry> Rows { get; } = new();

    public ValueTask<bool> TryAppendAsync(string? tenant, RecordEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ValueTask.FromResult(Rows.TryAdd(tenant, entry, row => row.RunId == entry.RunId && row.Revision == entry.Revision));
    }

    public ValueTask<IReadOnlyList<RecordEntry>> ReadAsync(string? tenant, string runId, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<RecordEntry>>([.. Rows.Where(tenant, entry => entry.RunId == runId).OrderBy(entry => entry.Revision)]);

    public ValueTask TruncateAsync(string? tenant, string runId, long revision, CancellationToken ct)
    {
        Rows.RemoveAll((rowTenant, entry) => rowTenant == tenant && entry.RunId == runId && entry.Revision > revision);
        return ValueTask.CompletedTask;
    }
}
