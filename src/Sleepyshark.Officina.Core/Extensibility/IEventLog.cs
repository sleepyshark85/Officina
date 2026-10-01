using Sleepyshark.Officina.Core.Events;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>The stored events, from which a reader catches up (EVT-03). Events are visible only within their tenant (SEC-02).</summary>
public interface IEventLog
{
    ValueTask AppendAsync(string? tenant, CoreEvent coreEvent, CancellationToken ct);

    /// <summary>A run's events after a sequence number, in order.</summary>
    ValueTask<IReadOnlyList<CoreEvent>> ReadAsync(string? tenant, string runId, long after, CancellationToken ct);
}
