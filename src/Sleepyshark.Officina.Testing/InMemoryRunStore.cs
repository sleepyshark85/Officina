using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Testing;

/// <summary>Keeps runs in memory, for tests.</summary>
public sealed class InMemoryRunStore : IRunStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(string? Tenant, string RunId), RunStatus> statuses = [];

    internal TenantRows<RunStarted> Rows { get; } = new();

    /// <summary>Every run, in every tenant, in the order started.</summary>
    public IReadOnlyList<RunStarted> Runs => Rows.All;

    public ValueTask RecordStartAsync(string? tenant, RunStarted run, CancellationToken ct)
    {
        Rows.Add(tenant, run);
        lock (gate)
        {
            statuses[(tenant, run.RunId)] = RunStatus.Running;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RecordStatusAsync(string? tenant, string runId, RunStatus status, CancellationToken ct)
    {
        lock (gate)
        {
            statuses[(tenant, runId)] = status;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<StoredRun?> ReadAsync(string? tenant, string runId, CancellationToken ct)
    {
        var found = Rows.Where(tenant, row => row.RunId == runId);
        var run = found.Count == 0 ? null : found[0];
        lock (gate)
        {
            return ValueTask.FromResult(run is null ? null : new StoredRun(run, statuses[(tenant, runId)]));
        }
    }
}
