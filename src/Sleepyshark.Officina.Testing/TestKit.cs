using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// Runs agents offline (TEST-01). The scripted model stands in for every provider of the configuration, so a definition
/// runs unchanged; the audit log, the human, the secret source and the clock are stand-ins too.
/// </summary>
public sealed class TestKit
{
    /// <param name="options">The configuration; the code defaults when omitted.</param>
    /// <param name="tools">The application's tools, by extension id.</param>
    /// <param name="gates">The application's gates, by extension id.</param>
    public TestKit(OfficinaOptions? options = null, IReadOnlyDictionary<string, ITool>? tools = null, IReadOnlyDictionary<string, IGate>? gates = null)
    {
        options ??= new OfficinaOptions();
        Runner = new AgentRunner(
            options,
            options.Providers.Keys.ToDictionary(name => name, IModelProvider (_) => Model),
            Runs,
            tools ?? new Dictionary<string, ITool>(),
            gates ?? new Dictionary<string, IGate>(),
            Audit,
            Human,
            new InMemorySecretSource(Secrets),
            Time);
    }

    public ScriptedModelProvider Model { get; } = new();

    /// <summary>The runs started so far, with their configuration.</summary>
    public InMemoryRunStore Runs { get; } = new();

    public InMemoryAuditLog Audit { get; } = new();

    public ScriptedHuman Human { get; } = new();

    /// <summary>The secrets tools can read, by name.</summary>
    public Dictionary<string, string> Secrets { get; } = [];

    public FakeTimeProvider Time { get; } = new();

    public AgentRunner Runner { get; }

    public Task<AgentResult> RunAsync(string agentName, string input, CancellationToken ct = default) =>
        Runner.RunAsync(agentName, input, ct);
}
