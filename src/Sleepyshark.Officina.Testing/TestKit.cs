using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// Runs agents offline (TEST-01). The scripted model stands in for every provider of the configuration, so a definition
/// runs unchanged; the human, the secret source and the clock are stand-ins too, and storage is in memory.
/// </summary>
public sealed class TestKit
{
    /// <param name="options">The configuration; the code defaults when omitted.</param>
    /// <param name="tools">The application's tools, by extension id.</param>
    /// <param name="gates">The application's gates, by extension id.</param>
    /// <param name="knowledge">The application's knowledge sources, by extension id.</param>
    /// <param name="checks">The application's checks, by extension id.</param>
    /// <param name="capabilities">What the scripted model claims to support; nothing when omitted.</param>
    /// <param name="shorteners">The application's history shorteners, by extension id.</param>
    /// <param name="human">The human; <see cref="Human"/>, who answers as scripted, when omitted.</param>
    /// <param name="patterns">The application's loop patterns, by extension id.</param>
    public TestKit(
        OfficinaOptions? options = null,
        IReadOnlyDictionary<string, ITool>? tools = null,
        IReadOnlyDictionary<string, IGate>? gates = null,
        IReadOnlyDictionary<string, IKnowledgeSource>? knowledge = null,
        ProviderCapabilities? capabilities = null,
        IReadOnlyDictionary<string, IHistoryShortener>? shorteners = null,
        IReadOnlyDictionary<string, ICheck>? checks = null,
        IHumanChannel? human = null,
        IReadOnlyDictionary<string, ILoopPattern>? patterns = null)
    {
        options ??= new OfficinaOptions();
        Model = new() { Capabilities = capabilities ?? ProviderCapabilities.None };
        Runner = new AgentRunner(
            options,
            options.Providers.Keys.ToDictionary(name => name, IModelProvider (_) => Model),
            Storage,
            tools ?? new Dictionary<string, ITool>(),
            gates ?? new Dictionary<string, IGate>(),
            checks ?? new Dictionary<string, ICheck>(),
            knowledge ?? new Dictionary<string, IKnowledgeSource>(),
            human ?? Human,
            new InMemorySecretSource(Secrets),
            Time,
            shorteners,
            patterns);
    }

    public ScriptedModelProvider Model { get; }

    /// <summary>The runs started so far with their configuration, their events, records and artifacts, and the audit log.</summary>
    public InMemoryStorage Storage { get; } = new();

    public ScriptedHuman Human { get; } = new();

    /// <summary>The secrets tools can read, by name.</summary>
    public Dictionary<string, string> Secrets { get; } = [];

    public FakeTimeProvider Time { get; } = new();

    public AgentRunner Runner { get; }

    public Task<AgentResult> RunAsync(string agentName, string input, CancellationToken ct = default) =>
        Runner.RunAsync(agentName, input, ct: ct);
}
