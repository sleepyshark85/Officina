using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// Runs agents, each in its own turns, and many at once (LOOP-01). Turns of the same agent run one at a time: work that
/// arrives during a turn waits for it (LOOP-02). Every run ends in a result, never an exception (REL-02).
/// </summary>
public sealed class AgentRunner
{
    /// <summary>The name an agent passed directly to <see cref="RunAsync(AgentDefinition, string, CancellationToken)"/> runs under.</summary>
    public const string InlineAgentName = "agent";

    private readonly IReadOnlyDictionary<string, IModelProvider> providers;
    private readonly IRunStore runs;
    private readonly IReadOnlyDictionary<string, ITool> tools;
    private readonly IReadOnlyDictionary<string, IGate> gates;
    private readonly IAuditLog audit;
    private readonly IHumanChannel human;
    private readonly ISecretSource secrets;
    private readonly TimeProvider time;
    private readonly ToolPipeline pipeline;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> turns = new(StringComparer.Ordinal);

    /// <summary>Validates the configuration in full, with the application's tools and gates; nothing runs if it has errors (CFG-06).</summary>
    /// <param name="options">The configuration, fixed for the runner's lifetime. A changed configuration needs a new runner (CFG-08).</param>
    /// <param name="providers">The provider implementations, by their name in <c>providers</c>.</param>
    /// <param name="runs">Where each run is recorded with the configuration it used (CFG-07).</param>
    /// <param name="tools">The application's tools, by the id that <c>extension:&lt;id&gt;</c> sources name.</param>
    /// <param name="gates">The application's gates, by the id that <c>extension:&lt;id&gt;</c> gates name.</param>
    /// <param name="audit">Where every write-tool attempt is recorded.</param>
    /// <param name="human">Who approves tool calls that need approval.</param>
    /// <param name="secrets">Where tools read credentials.</param>
    /// <param name="time">The clock for budgets, time limits and audit times.</param>
    /// <exception cref="ConfigurationException">The configuration has errors.</exception>
    public AgentRunner(
        OfficinaOptions options,
        IReadOnlyDictionary<string, IModelProvider> providers,
        IRunStore runs,
        IReadOnlyDictionary<string, ITool> tools,
        IReadOnlyDictionary<string, IGate> gates,
        IAuditLog audit,
        IHumanChannel human,
        ISecretSource secrets,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(runs);
        this.providers = providers;
        this.runs = runs;
        this.tools = tools;
        this.gates = gates;
        this.audit = audit;
        this.human = human;
        this.secrets = secrets;
        this.time = time;
        pipeline = new ToolPipeline(options, tools, gates, audit, human, secrets, time);
        Options = options;
    }

    public OfficinaOptions Options { get; }

    public Task<AgentResult> RunAsync(string agentName, string input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentName);
        if (!Options.Agents.ContainsKey(agentName))
        {
            throw ConfigurationException.UnknownAgent(agentName, Options.Agents.Keys);
        }

        return RunCoreAsync(agentName, Options, pipeline, input, ct);
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
        return RunCoreAsync(InlineAgentName, withAgent, new ToolPipeline(withAgent, tools, gates, audit, human, secrets, time), input, ct);
    }

    private async Task<AgentResult> RunCoreAsync(string name, OfficinaOptions resolved, ToolPipeline tools, string input, CancellationToken ct)
    {
        var agent = resolved.Agents[name];
        var profile = resolved.Models[agent.Model];
        var provider = providers.TryGetValue(profile.Provider, out var registered)
            ? registered
            : throw new InvalidOperationException($"Agent \"{name}\" uses provider \"{profile.Provider}\", which has no implementation registered.");

        var started = new RunStarted(Guid.CreateVersion7().ToString(), name, CoreVersion.Value, resolved);
        var instructions = InstructionPlaceholders.Fill(agent.Instructions, resolved.Project, name, agent);

        // The caller arrives with admission (S09); until then runs act for an anonymous caller.
        var turn = new Turn(new ToolContext(started.RunId, name, Caller.Anonymous), resolved, provider, tools, instructions, input, time);
        var oneAtATime = turns.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        var entered = false;
        try
        {
            await oneAtATime.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            await runs.RecordStartAsync(started, ct).ConfigureAwait(false);
            return await turn.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return turn.HandOff(HandoffReason.RequestedByHuman, "the turn was cancelled");
        }
        catch (Exception exception)
        {
            return turn.Fail(exception);
        }
        finally
        {
            if (entered)
            {
                oneAtATime.Release();
            }
        }
    }
}
