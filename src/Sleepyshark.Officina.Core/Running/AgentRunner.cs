using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// Runs agents, each in its own turns, and many at once (LOOP-01). Turns of the same agent run one at a time: work that
/// arrives during a turn waits for it (LOOP-02), while messages sent during a turn join it (CTX-08). Every run ends in a
/// result, never an exception (REL-02).
/// </summary>
public sealed class AgentRunner
{
    private readonly IReadOnlyDictionary<string, IModelProvider> providers;
    private readonly IRunStore runs;
    private readonly TimeProvider time;
    private readonly ToolPipeline pipeline;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> turns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Message>> inboxes = new(StringComparer.Ordinal);

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
        this.time = time;
        pipeline = new ToolPipeline(options, tools, gates, audit, human, secrets, time);
        Options = options;
    }

    public OfficinaOptions Options { get; }

    /// <param name="agentName">The agent, by its name in <c>agents</c>.</param>
    /// <param name="input">The work.</param>
    /// <param name="caller">Who the run acts for; an anonymous caller when null.</param>
    /// <param name="ct">Cancels the run, which then ends in a handoff.</param>
    public Task<AgentResult> RunAsync(string agentName, string input, Caller? caller = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentName);
        if (!Options.Agents.ContainsKey(agentName))
        {
            throw ConfigurationException.UnknownAgent(agentName, Options.Agents.Keys);
        }

        return RunCoreAsync(agentName, input, caller ?? Caller.Anonymous, ct);
    }

    /// <summary>
    /// Sends the agent a message (CTX-08). Its running turn adds it to the history before its next model call, labelled
    /// with the sender (MSG-04); with no turn running, the agent's next turn starts with it.
    /// </summary>
    public void Send(string agentName, Sender from, string text)
    {
        ArgumentNullException.ThrowIfNull(agentName);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(text);
        if (!Options.Agents.ContainsKey(agentName))
        {
            throw ConfigurationException.UnknownAgent(agentName, Options.Agents.Keys);
        }

        if (from.Kind == SenderKind.Agent && string.IsNullOrEmpty(from.Agent))
        {
            throw new ArgumentException("A message from an agent needs the agent's name.", nameof(from));
        }

        Inbox(agentName).Enqueue(Labels.From(from, text));
    }

    private ConcurrentQueue<Message> Inbox(string agentName) => inboxes.GetOrAdd(agentName, _ => new());

    private async Task<AgentResult> RunCoreAsync(string name, string input, Caller caller, CancellationToken ct)
    {
        var agent = Options.Agents[name];
        var profile = Options.Models[agent.Model];
        var provider = providers.TryGetValue(profile.Provider, out var registered)
            ? registered
            : throw new InvalidOperationException($"Agent \"{name}\" uses provider \"{profile.Provider}\", which has no implementation registered.");

        var started = new RunStarted(Guid.CreateVersion7().ToString(), name, CoreVersion.Value, Options);
        var instructions = InstructionPlaceholders.Fill(agent.Instructions, Options.Project, name, agent);

        var turn = new Turn(new ToolContext(started.RunId, name, caller), Options, provider, pipeline, instructions, input, Inbox(name), time);
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
