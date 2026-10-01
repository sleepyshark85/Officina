using System.Text;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>Runs an agent as a single model call: instructions and input in, completed output back.</summary>
/// <remarks>The turn loop (S04) replaces this with tools, stop reasons, budgets and handoffs.</remarks>
public sealed class AgentRunner
{
    /// <summary>The name an agent passed directly to <see cref="RunAsync(AgentDefinition, string, CancellationToken)"/> runs under.</summary>
    public const string InlineAgentName = "agent";

    private readonly IReadOnlyDictionary<string, IModelProvider> providers;
    private readonly IRunStore runs;

    /// <summary>Validates the configuration in full; nothing runs if it has errors (CFG-06).</summary>
    /// <param name="options">The configuration, fixed for the runner's lifetime. A changed configuration needs a new runner (CFG-08).</param>
    /// <param name="providers">The provider implementations, by their name in <c>providers</c>.</param>
    /// <param name="runs">Where each run is recorded with the configuration it used (CFG-07).</param>
    /// <exception cref="ConfigurationException">The configuration has errors.</exception>
    public AgentRunner(OfficinaOptions options, IReadOnlyDictionary<string, IModelProvider> providers, IRunStore runs)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(runs);
        ThrowIfInvalid(options);
        Options = options;
        this.providers = providers;
        this.runs = runs;
    }

    public OfficinaOptions Options { get; }

    public Task<AgentResult> RunAsync(string agentName, string input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentName);
        var agent = Options.Agents.TryGetValue(agentName, out var found)
            ? found
            : throw ConfigurationException.UnknownAgent(agentName, Options.Agents.Keys);
        return RunCoreAsync(agentName, agent, Options, input, ct);
    }

    /// <summary>Runs an agent that is not in the configuration, as if it were defined there as <see cref="InlineAgentName"/>.</summary>
    /// <exception cref="ConfigurationException">The agent does not validate, or the configuration already has an agent of that name.</exception>
    public Task<AgentResult> RunAsync(AgentDefinition agent, string input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (Options.Agents.ContainsKey(InlineAgentName))
        {
            throw new ConfigurationException([new ConfigurationError(ValidationPhase.References, $"agents.{InlineAgentName}",
                "is already defined, so an agent passed directly cannot run under its name.", "Run the configured agent by name, or rename it.")]);
        }

        var withAgent = Options with { Agents = new Dictionary<string, AgentDefinition>(Options.Agents) { [InlineAgentName] = agent } };
        ThrowIfInvalid(withAgent);
        return RunCoreAsync(InlineAgentName, agent, withAgent, input, ct);
    }

    private async Task<AgentResult> RunCoreAsync(string name, AgentDefinition agent, OfficinaOptions resolved, string input, CancellationToken ct)
    {
        var profile = resolved.Models[agent.Model];
        var provider = providers.TryGetValue(profile.Provider, out var registered)
            ? registered
            : throw new InvalidOperationException($"Agent \"{name}\" uses provider \"{profile.Provider}\", which has no implementation registered.");

        var started = new RunStarted(Guid.CreateVersion7().ToString(), name, CoreVersion.Value, resolved);
        await runs.RecordStartAsync(started, ct).ConfigureAwait(false);

        var instructions = InstructionPlaceholders.Fill(agent.Instructions, resolved.Project, name, agent);
        var request = new ModelRequest(profile, instructions, [Message.User(input)]);
        var output = new StringBuilder();
        await foreach (var modelEvent in provider.StreamAsync(request, ct).WithCancellation(ct))
        {
            if (modelEvent is TextDelta delta)
            {
                output.Append(delta.Text);
            }
        }

        return AgentResult.Completed(output.ToString());
    }

    private static void ThrowIfInvalid(OfficinaOptions candidate)
    {
        var errors = candidate.Validate();
        if (errors.Count > 0)
        {
            throw new ConfigurationException(errors);
        }
    }
}
