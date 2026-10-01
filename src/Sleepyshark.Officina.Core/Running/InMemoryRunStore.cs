using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>Keeps runs in memory, for tests, dry runs and hosts without storage.</summary>
public sealed class InMemoryRunStore : IRunStore
{
    private readonly Lock gate = new();
    private readonly List<RunStarted> runs = [];

    public IReadOnlyList<RunStarted> Runs
    {
        get
        {
            lock (gate)
            {
                return [.. runs];
            }
        }
    }

    public ValueTask RecordStartAsync(RunStarted run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (gate)
        {
            runs.Add(run);
        }

        return ValueTask.CompletedTask;
    }
}
