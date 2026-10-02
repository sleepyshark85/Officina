using Sleepyshark.Officina.Core.Memory;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// Where project memory is kept, as its changes: the log of proposals and of the decisions on them (MEM-03, MEM-04).
/// A scope is a project, an owner or a tenant. Changes are visible only within their tenant (SEC-02).
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// Appends a change in one all-or-nothing write, unless the scope already has a change with its revision: then
    /// another change came first, nothing is written, and the result is false (CONC-01).
    /// </summary>
    ValueTask<bool> TryAppendAsync(string? tenant, MemoryChange change, CancellationToken ct);

    /// <summary>A scope's changes in revision order.</summary>
    ValueTask<IReadOnlyList<MemoryChange>> ReadAsync(string? tenant, string scope, CancellationToken ct);

    /// <summary>Deletes the changes after a revision, when a run goes back to a checkpoint that recorded it (RUN-08).</summary>
    ValueTask TruncateAsync(string? tenant, string scope, long revision, CancellationToken ct);
}
