using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Testing;

/// <summary>Runs agents offline. The scripted model stands in for the default profile's provider, so a definition runs unchanged (TEST-01).</summary>
public sealed class TestKit
{
    public TestKit()
    {
        Runner = new AgentRunner(new Dictionary<string, IModelProvider> { [new ModelProfile().Provider] = Model });
    }

    public ScriptedModelProvider Model { get; } = new();

    public AgentRunner Runner { get; }

    public Task<AgentResult> RunAsync(AgentDefinition agent, string input, CancellationToken ct = default) =>
        Runner.RunAsync(agent, input, ct);
}
