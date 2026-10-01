using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>Secrets held in memory, for tests, in place of the environment or a vault.</summary>
public sealed class InMemorySecretSource(IReadOnlyDictionary<string, string> secrets) : ISecretSource
{
    public InMemorySecretSource()
        : this(new Dictionary<string, string>())
    {
    }

    public ValueTask<string> GetAsync(string name, CancellationToken ct) =>
        secrets.TryGetValue(name, out var value)
            ? ValueTask.FromResult(value)
            : throw new KeyNotFoundException($"The secret {name} is not set.");
}
