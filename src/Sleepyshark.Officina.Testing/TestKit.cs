using Sleepyshark.Officina.Core.Capabilities;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// Runs agents offline. The scripted model stands in for every provider of the configuration, so a
/// definition runs unchanged (TEST-01).
/// </summary>
public sealed class TestKit
{
    /// <param name="options">The configuration; the code defaults when omitted.</param>
    /// <param name="capabilities">The capabilities available to the configuration.</param>
    public TestKit(OfficinaOptions? options = null, CapabilityRegistry? capabilities = null)
    {
        options ??= new OfficinaOptions();
        var providers = options.Providers.Keys.ToDictionary(name => name, IModelProvider (_) => Model);
        Runner = new AgentRunner(options, providers, capabilities, Runs);
    }

    public ScriptedModelProvider Model { get; } = new();

    public InMemoryRunStore Runs { get; } = new();

    public AgentRunner Runner { get; }

    public Task<AgentResult> RunAsync(AgentDefinition agent, string input, CancellationToken ct = default) =>
        Runner.RunAsync(agent, input, ct);

    public Task<AgentResult> RunAsync(string agentName, string input, CancellationToken ct = default) =>
        Runner.RunAsync(agentName, input, ct);
}
