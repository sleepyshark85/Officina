using System.Collections.Concurrent;
using System.Diagnostics;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Observability;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// Runs agents, each in its own turns, and many at once (LOOP-01). Work passes admission first (ING-01), whichever way it
/// arrives (TRG-01); a rejection is a result with its reason, never an error (ING-04). Turns of the same agent run one at
/// a time: work that arrives during a turn waits for it (LOOP-02), while messages sent during a turn join it (CTX-08).
/// Every run ends in a result, never an exception (REL-02). Runs keep no conversation, so requests and batches are
/// stateless (TRG-04).
/// </summary>
public sealed class AgentRunner
{
    private readonly IReadOnlyDictionary<string, IModelProvider> providers;
    private readonly IStorage storage;
    private readonly TimeProvider time;
    private readonly ToolPipeline pipeline;
    private readonly IReadOnlyDictionary<string, IKnowledgeSource> knowledge;
    private readonly Dictionary<string, IHistoryShortener> shortening;
    private readonly Admission admission;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> turns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(Sender From, string Text)>> inboxes = new(StringComparer.Ordinal);

    /// <summary>Validates the configuration in full, with the application's tools and gates; nothing runs if it has errors (CFG-06).</summary>
    /// <param name="options">The configuration, fixed for the runner's lifetime. A changed configuration needs a new runner (CFG-08).</param>
    /// <param name="providers">The provider implementations, by their name in <c>providers</c>.</param>
    /// <param name="storage">
    /// Where each run is recorded with the configuration it used (CFG-07), with its events and audit entries, and where
    /// conversations are kept.
    /// </param>
    /// <param name="tools">
    /// The application's tools, by the id that <c>extension:&lt;id&gt;</c> sources name, and the tool servers' tools, by
    /// the <c>&lt;server&gt;/&lt;tool&gt;</c> that <c>mcp:</c> sources name.
    /// </param>
    /// <param name="gates">The application's gates, by the id that <c>extension:&lt;id&gt;</c> gates name.</param>
    /// <param name="knowledge">The application's knowledge sources, by the id that <c>extension:&lt;id&gt;</c> sources name.</param>
    /// <param name="human">Who approves tool calls that need approval.</param>
    /// <param name="secrets">Where tools read credentials.</param>
    /// <param name="time">The clock for budgets, time limits and audit times.</param>
    /// <param name="shorteners">The application's history shorteners, by the id that <c>extension:&lt;id&gt;</c> shortenings name.</param>
    /// <exception cref="ConfigurationException">The configuration has errors.</exception>
    public AgentRunner(
        OfficinaOptions options,
        IReadOnlyDictionary<string, IModelProvider> providers,
        IStorage storage,
        IReadOnlyDictionary<string, ITool> tools,
        IReadOnlyDictionary<string, IGate> gates,
        IReadOnlyDictionary<string, IKnowledgeSource> knowledge,
        IHumanChannel human,
        ISecretSource secrets,
        TimeProvider time,
        IReadOnlyDictionary<string, IHistoryShortener>? shorteners = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storage);
        this.providers = providers;
        this.storage = storage;
        this.time = time;
        Events = new EventBus(storage.Events, options.Storage, time);
        pipeline = new ToolPipeline(options, tools, gates, knowledge, storage.Audit, Events, human, secrets, time);
        this.knowledge = knowledge;
        shortening = Shortening(options, providers, shorteners ?? new Dictionary<string, IHistoryShortener>());
        admission = new Admission(options.Policies);
        Options = options;
    }

    public OfficinaOptions Options { get; }

    /// <summary>What the runs do, live and from the start of each stored run (EVT-01, EVT-03).</summary>
    public EventBus Events { get; }

    /// <summary>Runs a single request.</summary>
    /// <param name="agentName">The agent, by its name in <c>agents</c>.</param>
    /// <param name="input">The work.</param>
    /// <param name="caller">Who the run acts for; an anonymous caller when null.</param>
    /// <param name="ct">Cancels the run, which then ends in a handoff.</param>
    public Task<AgentResult> RunAsync(string agentName, string input, Caller? caller = null, CancellationToken ct = default) =>
        RunAsync(new Work(agentName, input) { Caller = caller ?? Caller.Anonymous }, ct);

    /// <summary>Runs one work item, however it arrived.</summary>
    /// <param name="work">The work.</param>
    /// <param name="ct">Cancels the run, which then ends in a handoff.</param>
    public Task<AgentResult> RunAsync(Work work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        KnownAgent(work.Agent);
        return RunCoreAsync(work, ct);
    }

    /// <summary>
    /// Runs a batch: each input is admitted and run as its own work item. The results are in the order of the inputs, and
    /// an input that fails or is rejected does not stop the others (TRG-03).
    /// </summary>
    /// <param name="agentName">The agent, by its name in <c>agents</c>.</param>
    /// <param name="inputs">The inputs.</param>
    /// <param name="caller">Who the runs act for; an anonymous caller when null.</param>
    /// <param name="ct">Cancels the runs, which then end in handoffs.</param>
    public async Task<IReadOnlyList<AgentResult>> RunBatchAsync(string agentName, IReadOnlyList<string> inputs, Caller? caller = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        KnownAgent(agentName);
        return await Task.WhenAll(inputs.Select(input => RunCoreAsync(new Work(agentName, input) { Trigger = Trigger.Batch, Caller = caller ?? Caller.Anonymous }, ct)))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the agent a message (CTX-08). Its running turn adds it to the history before its next model call, labelled
    /// with the sender (MSG-04); with no turn running, the agent's next turn starts with it.
    /// </summary>
    public void Send(string agentName, Sender from, string text)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(text);
        KnownAgent(agentName);
        if (from.Kind == SenderKind.Agent && string.IsNullOrEmpty(from.Agent))
        {
            throw new ArgumentException("A message from an agent needs the agent's name.", nameof(from));
        }

        Inbox(agentName).Enqueue((from, text));
    }

    /// <summary>
    /// What shortens the history of each agent whose strategy is <c>shortened</c> (HIST-01): its model provider, which
    /// must be able to, or a shortener the application registers.
    /// </summary>
    /// <exception cref="ConfigurationException">A shortening is not available.</exception>
    private static Dictionary<string, IHistoryShortener> Shortening(
        OfficinaOptions options, IReadOnlyDictionary<string, IModelProvider> providers, IReadOnlyDictionary<string, IHistoryShortener> shorteners)
    {
        var found = new Dictionary<string, IHistoryShortener>(StringComparer.Ordinal);
        var errors = new List<ConfigurationError>();
        foreach (var (name, agent) in options.Agents.Where(agent => agent.Value.Context.History.Strategy == HistoryStrategy.Shortened))
        {
            var path = $"agents.{name}.context.history.shortening";
            var provider = options.Models[agent.Model].Provider;
            var id = agent.Context.History.ExtensionId();
            if ((id is null ? providers.GetValueOrDefault(provider) as IHistoryShortener : shorteners.GetValueOrDefault(id)) is { } shortener)
            {
                found[name] = shortener;
            }
            else if (id is not null)
            {
                errors.Add(new(ValidationPhase.References, path, $"history shortener extension \"{id}\" is not registered.",
                    $"Register the application's history shortener under the id \"{id}\"."));
            }
            else if (providers.ContainsKey(provider))
            {
                errors.Add(new(ValidationPhase.Provider, path, $"provider \"{provider}\" cannot shorten history.",
                    "Use extension:<id> for a shortener the application registers."));
            }
        }

        return errors.Count > 0 ? throw new ConfigurationException([.. errors.OrderBy(error => error.Phase)]) : found;
    }

    private void KnownAgent(string agentName)
    {
        ArgumentNullException.ThrowIfNull(agentName);
        if (!Options.Agents.ContainsKey(agentName))
        {
            throw ConfigurationException.UnknownAgent(agentName, Options.Agents.Keys);
        }
    }

    private ConcurrentQueue<(Sender From, string Text)> Inbox(string agentName) => inboxes.GetOrAdd(agentName, _ => new());

    private async Task<AgentResult> RunCoreAsync(Work work, CancellationToken ct)
    {
        var name = work.Agent;
        var agent = Options.Agents[name];
        var profile = Options.Models[agent.Model];
        var provider = providers.TryGetValue(profile.Provider, out var registered)
            ? registered
            : throw new InvalidOperationException($"Agent \"{name}\" uses provider \"{profile.Provider}\", which has no implementation registered.");

        var (admitted, masker, rejection) = admission.Admit(work, agent);
        if (rejection is not null)
        {
            return new AgentResult(AgentOutcome.Rejected, rejection, new TurnStatistics(0, 0, Usage.None, 0m, TimeSpan.Zero), [], []);
        }

        var context = new ToolContext(work.RunId, name, work.Caller) { Masker = masker };
        var instructions = InstructionPlaceholders.Fill(agent.Instructions, Options.Project, name, agent);
        var turn = new Turn(
            context, Options, provider, pipeline, knowledge, storage.Conversations, shortening.GetValueOrDefault(name), Events, instructions, admitted, Inbox(name), time);
        var oneAtATime = turns.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        var entered = false;
        Activity? activity = null;
        AgentResult result;
        try
        {
            await oneAtATime.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            activity = Telemetry.StartTurn(context); // after the wait, so the span covers the turn only
            await storage.DeleteExpiredAsync(Options.Storage.Retention, time.GetUtcNow(), ct).ConfigureAwait(false);
            var started = new RunStarted(context.RunId, name, context.Caller.Id, time.GetUtcNow(), CoreVersion.Value, Options);
            await storage.Runs.RecordStartAsync(context.Caller.Tenant, started, ct).ConfigureAwait(false);
            await Events.PublishAsync(context, new TurnStarted(), ct).ConfigureAwait(false);
            result = await turn.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = turn.HandOff(HandoffReason.RequestedByHuman, "the turn was cancelled");
        }
        catch (Exception exception)
        {
            result = turn.Fail(exception);
        }
        finally
        {
            if (entered)
            {
                oneAtATime.Release();
            }
        }

        try
        {
            // Published even when the turn was cancelled, so readers see every turn end.
            await Events.PublishAsync(context, new TurnEnded(result.Outcome, result.Handoff?.Reason), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = turn.Fail(exception);
        }

        var reason = result.Handoff?.Reason.ToString() ?? (result.Outcome == AgentOutcome.Failed ? result.Output : "");
        Telemetry.TurnEnded(activity, context, result.Outcome.ToString(), result.Handoff?.Reason.ToString());
        OfficinaLog.Log.TurnEnded(context.RunId, name, result.Outcome.ToString(), reason);
        activity?.Dispose();
        return result;
    }
}
