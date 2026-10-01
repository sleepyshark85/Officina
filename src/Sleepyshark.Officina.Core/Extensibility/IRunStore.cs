using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>Where runs are stored, each with the configuration it used (CFG-07).</summary>
public interface IRunStore
{
    /// <param name="tenant">The owner's tenant, or null; the run is visible only within it (SEC-02).</param>
    /// <param name="run">How the run started.</param>
    /// <param name="ct">Cancels the write.</param>
    ValueTask RecordStartAsync(string? tenant, RunStarted run, CancellationToken ct);
}
