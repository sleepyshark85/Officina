using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// Runs agents offline. The scripted model stands in for every provider of the configuration, so a definition runs
/// unchanged (TEST-01).
/// </summary>
public sealed class TestKit
{
    /// <param name="options">The configuration; the code defaults when omitted.</param>
    public TestKit(OfficinaOptions? options = null)
    {
        options ??= new OfficinaOptions();
        Runner = new AgentRunner(options, options.Providers.Keys.ToDictionary(name => name, IModelProvider (_) => Model), Runs);
    }

    public ScriptedModelProvider Model { get; } = new();

    /// <summary>The runs started so far, with their configuration.</summary>
    public InMemoryRunStore Runs { get; } = new();

    public AgentRunner Runner { get; }

    public Task<AgentResult> RunAsync(AgentDefinition agent, string input, CancellationToken ct = default) =>
        Runner.RunAsync(agent, input, ct);

    public Task<AgentResult> RunAsync(string agentName, string input, CancellationToken ct = default) =>
        Runner.RunAsync(agentName, input, ct);
}
