using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Cli;

/// <summary>Secrets read from environment variables, by the variable's name (SEC-05).</summary>
internal sealed class EnvironmentSecrets(IReadOnlyDictionary<string, string> variables) : ISecretSource
{
    public ValueTask<string> GetAsync(string name, CancellationToken ct) =>
        variables.TryGetValue(name, out var value)
            ? ValueTask.FromResult(value)
            : throw new KeyNotFoundException($"The secret {name} is not set: set the environment variable {name}.");
}
