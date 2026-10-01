using System.Text;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>Runs an agent as a single model call: instructions and input in, completed output back.</summary>
/// <remarks>The turn loop (S04) replaces this with tools, stop reasons, budgets and handoffs.</remarks>
public sealed class AgentRunner
{
    private readonly IReadOnlyDictionary<string, IModelProvider> providers;
    private readonly Dictionary<string, ModelProfile> profiles;

    /// <param name="providers">The available providers, by the name profiles use to refer to them.</param>
    /// <param name="profiles">Named model profiles. A <c>default</c> profile is added when missing.</param>
    public AgentRunner(
        IReadOnlyDictionary<string, IModelProvider> providers,
        IReadOnlyDictionary<string, ModelProfile>? profiles = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        this.providers = providers;
        this.profiles = new Dictionary<string, ModelProfile>(profiles ?? new Dictionary<string, ModelProfile>());
        this.profiles.TryAdd(ModelProfile.DefaultName, new ModelProfile());
    }

    public async Task<AgentResult> RunAsync(AgentDefinition agent, string input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.Instructions);

        var profile = profiles.TryGetValue(agent.Model, out var found)
            ? found
            : throw new InvalidOperationException($"The agent uses model profile '{agent.Model}', which is not defined.");
        var provider = providers.TryGetValue(profile.Provider, out var registered)
            ? registered
            : throw new InvalidOperationException($"Model profile '{agent.Model}' uses provider '{profile.Provider}', which is not registered.");

        var request = new ModelRequest(profile, agent.Instructions, [Message.User(input)]);
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
}
