using System.Text;
using Sleepyshark.Officina.Core.Capabilities;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Configuration.Placeholders;
using Sleepyshark.Officina.Core.Configuration.Validation;
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
    private readonly SettingsModel model;
    private readonly RunConfiguration configuration;

    /// <summary>Validates the configuration in full; nothing runs if it has errors (CFG-06).</summary>
    /// <param name="options">The resolved configuration. It is fixed for the runner's lifetime; a changed configuration needs a new runner (CFG-08).</param>
    /// <param name="providers">The provider implementations, by their name in <c>providers</c>.</param>
    /// <param name="capabilities">The capabilities the host makes available.</param>
    /// <param name="runs">Where runs are recorded. By default they are kept in memory.</param>
    /// <exception cref="ConfigurationException">The configuration has errors.</exception>
    public AgentRunner(
        OfficinaOptions options,
        IReadOnlyDictionary<string, IModelProvider> providers,
        CapabilityRegistry? capabilities = null,
        IRunStore? runs = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(providers);
        this.providers = providers;
        model = SettingsModel.For(capabilities ?? CapabilityRegistry.Empty);
        Options = options;
        Runs = runs ?? new InMemoryRunStore();
        ThrowIfInvalid(options);
        configuration = RunConfiguration.Capture(options, model);
    }

    public OfficinaOptions Options { get; }

    public IRunStore Runs { get; }

    /// <summary>Runs an agent of the configuration.</summary>
    public Task<AgentResult> RunAsync(string agentName, string input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentName);
        var agent = Options.Agents.TryGetValue(agentName, out var found)
            ? found
            : throw new InvalidOperationException($"There is no agent \"{agentName}\". Agents: {string.Join(", ", Options.Agents.Keys)}.");
        return RunCoreAsync(agentName, agent, configuration, input, ct);
    }

    /// <summary>Runs an agent that is not in the configuration, as if it were defined there as <see cref="InlineAgentName"/>.</summary>
    /// <exception cref="ConfigurationException">The agent does not validate with this configuration.</exception>
    public Task<AgentResult> RunAsync(AgentDefinition agent, string input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var withAgent = Options with { Agents = Options.Agents.With(InlineAgentName, agent) };
        ThrowIfInvalid(withAgent);
        return RunCoreAsync(InlineAgentName, agent, RunConfiguration.Capture(withAgent, model), input, ct);
    }

    private async Task<AgentResult> RunCoreAsync(string name, AgentDefinition agent, RunConfiguration resolved, string input, CancellationToken ct)
    {
        var profile = agent.Model.Profile ?? Options.Models[agent.Model.Name!];
        var provider = providers.TryGetValue(profile.Provider, out var registered)
            ? registered
            : throw new InvalidOperationException($"Agent \"{name}\" uses provider \"{profile.Provider}\", which has no implementation registered.");

        await Runs.RecordStartAsync(new RunStarted(Guid.CreateVersion7().ToString(), name, resolved), ct).ConfigureAwait(false);

        var instructions = PlaceholderRules.FillStablePrefix(agent.Instructions, new StablePrefixValues(Options.Project, name, agent));
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

    private void ThrowIfInvalid(OfficinaOptions candidate)
    {
        var context = new ValidationContext(candidate, model)
        {
            DescribeProvider = name => providers.TryGetValue(name, out var provider) ? provider.Capabilities : null,
        };
        var errors = new ConfigurationValidator(model).Validate(context);
        if (errors.Count > 0)
        {
            throw new ConfigurationException(errors);
        }
    }
}
