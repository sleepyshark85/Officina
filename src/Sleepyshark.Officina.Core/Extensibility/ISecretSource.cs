namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>Where secrets are read, by name, at the moment they are used (CFG-09, SEC-05).</summary>
public interface ISecretSource
{
    ValueTask<string> GetAsync(string name, CancellationToken ct);
}
