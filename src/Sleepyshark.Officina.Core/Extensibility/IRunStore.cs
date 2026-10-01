using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>Stores runs. For now it records how each run started, with its resolved configuration (CFG-07); storage (S08) adds the rest.</summary>
public interface IRunStore
{
    ValueTask RecordStartAsync(RunStarted run, CancellationToken ct);
}
