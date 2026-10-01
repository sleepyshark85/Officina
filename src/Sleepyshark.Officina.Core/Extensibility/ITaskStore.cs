using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>Where task boards are kept, as their changes (TASK-07). Changes are visible only within their tenant (SEC-02).</summary>
public interface ITaskStore
{
    /// <summary>
    /// Appends a change in one all-or-nothing write, unless the run's board already has a change with its revision: then
    /// another change came first, nothing is written, and the result is false (TASK-04, CONC-01).
    /// </summary>
    ValueTask<bool> TryAppendAsync(string? tenant, TaskChange change, CancellationToken ct);

    /// <summary>A run's board, as its changes in revision order.</summary>
    ValueTask<IReadOnlyList<TaskChange>> ReadAsync(string? tenant, string runId, CancellationToken ct);
}
