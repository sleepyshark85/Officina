using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Testing;

/// <summary>Keeps runs in memory, for tests.</summary>
public sealed class InMemoryRunStore : IRunStore
{
    internal TenantRows<RunStarted> Rows { get; } = new();

    /// <summary>Every run, in every tenant, in the order started.</summary>
    public IReadOnlyList<RunStarted> Runs => Rows.All;

    public ValueTask RecordStartAsync(string? tenant, RunStarted run, CancellationToken ct)
    {
        Rows.Add(tenant, run);
        return ValueTask.CompletedTask;
    }
}
