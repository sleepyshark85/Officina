using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Testing;

/// <summary>Keeps runs in memory, for tests. Durable stores (S08) implement <see cref="IRunStore"/> in their own projects.</summary>
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
