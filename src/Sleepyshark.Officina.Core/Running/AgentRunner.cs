using System.Collections.Concurrent;
using System.Diagnostics;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Observability;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Tasks;
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
    private readonly IReadOnlyDictionary<string, ICheck> checks;
    private readonly IReadOnlyDictionary<string, IKnowledgeSource> knowledge;
    private readonly Dictionary<string, IHistoryShortener> shortening;
    private readonly Admission admission;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> turns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(Sender From, string Text)>> inboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> paused = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> cancels = new(StringComparer.Ordinal);

    /// <summary>Validates the configuration in full, with the application's tools, gates and checks; nothing runs if it has errors (CFG-06).</summary>
    /// <param name="options">The configuration, fixed for the runner's lifetime. A changed configuration needs a new runner (CFG-08).</param>
    /// <param name="providers">The provider implementations, by their name in <c>providers</c>.</param>
    /// <param name="storage">
    /// Where each run is recorded with the configuration it used (CFG-07), with its events, audit entries, record and
    /// artifacts, and where conversations are kept.
    /// </param>
    /// <param name="tools">
    /// The application's tools, by the id that <c>extension:&lt;id&gt;</c> sources name, and the tool servers' tools, by
    /// the <c>&lt;server&gt;/&lt;tool&gt;</c> that <c>mcp:</c> sources name.
    /// </param>
    /// <param name="gates">The application's gates, by the id that <c>extension:&lt;id&gt;</c> gates name.</param>
    /// <param name="checks">The application's checks, by the id that <c>extension:&lt;id&gt;</c> checks name.</param>
    /// <param name="knowledge">The application's knowledge sources, by the id that <c>extension:&lt;id&gt;</c> sources name.</param>
    /// <param name="human">Who approves tool calls that need approval.</param>
    /// <param name="secrets">
    /// Where tools read credentials. Pass the <see cref="KnownSecrets"/> the providers and tool servers read theirs from, so
    /// those are removed from what tools return too (INV-06).
    /// </param>
    /// <param name="time">The clock for budgets, time limits and audit times.</param>
    /// <param name="shorteners">The application's history shorteners, by the id that <c>extension:&lt;id&gt;</c> shortenings name.</param>
    /// <exception cref="ConfigurationException">The configuration has errors.</exception>
    public AgentRunner(
        OfficinaOptions options,
        IReadOnlyDictionary<string, IModelProvider> providers,
        IStorage storage,
        IReadOnlyDictionary<string, ITool> tools,
        IReadOnlyDictionary<string, IGate> gates,
        IReadOnlyDictionary<string, ICheck> checks,
        IReadOnlyDictionary<string, IKnowledgeSource> knowledge,
        IHumanChannel human,
        ISecretSource secrets,
        TimeProvider time,
        IReadOnlyDictionary<string, IHistoryShortener>? shorteners = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(checks);
        this.providers = providers;
        this.storage = storage;
        this.time = time;
        Events = new EventBus(storage.Events, options.Storage, time);
        pipeline = new ToolPipeline(options, tools, gates, knowledge, checks, storage, Events, human, secrets, time);
        this.checks = checks;
        this.knowledge = knowledge;
        ProviderToolsSupported(options, providers);
        shortening = Shortening(options, providers, shorteners ?? new Dictionary<string, IHistoryShortener>());
        admission = new Admission(options.Policies, time);
        Options = options;
    }

    public OfficinaOptions Options { get; }

    /// <summary>What the runs do, live and from the start of each stored run (EVT-01, EVT-03).</summary>
    public EventBus Events { get; }

    /// <summary>A run's task board, as the owner or the host changes it (TASK-08, WS-03).</summary>
    /// <param name="tenant">The run's tenant.</param>
    /// <param name="runId">The run.</param>
    /// <exception cref="InvalidOperationException">The task board is off.</exception>
    public TaskBoard Board(string? tenant, string runId) => pipeline.Board(tenant, runId);

    /// <summary>How write tool calls are decided (HITL-01). The owner may change it during a run; each agent's next call uses it.</summary>
    public PermissionMode PermissionMode
    {
        get => pipeline.PermissionMode;
        set => pipeline.PermissionMode = value;
    }

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
    /// <exception cref="ArgumentException">The work is for a task, and the task board is off or has no such task.</exception>
    public async Task<AgentResult> RunAsync(Work work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        KnownAgent(work.Agent);
        if (work.TaskId is not null)
        {
            if (!Options.Capabilities.TaskBoard.Enabled)
            {
                throw new ArgumentException("The work is for a task, but the task board is off.", nameof(work));
            }

            if (!(await Board(work.Caller.Tenant, work.RunId).ReadAsync(ct).ConfigureAwait(false)).Any(task => task.Id == work.TaskId))
            {
                throw new ArgumentException($"The work is for task {work.TaskId}, which is not on the board.", nameof(work));
            }
        }

        return await RunCoreAsync(work, ct).ConfigureAwait(false);
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

    /// <summary>Pauses the agent before its next model call, until it is resumed (RUN-06). Other agents go on.</summary>
    public void Pause(string agentName)
    {
        KnownAgent(agentName);
        paused.TryAdd(agentName, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public void Resume(string agentName)
    {
        KnownAgent(agentName);
        if (paused.TryRemove(agentName, out var gate))
        {
            gate.SetResult();
        }
    }

    /// <summary>
    /// Cancels the agent's turns, running or waiting to run (RUN-06). Each ends in a handoff within
    /// <c>run.cancelWithin</c>. Work sent to the agent afterwards runs as usual.
    /// </summary>
    public void Cancel(string agentName)
    {
        KnownAgent(agentName);
        if (cancels.TryRemove(agentName, out var cancel))
        {
            cancel.Cancel();
        }
    }

    /// <summary>MDL-06: each agent's provider must run the provider tools the agent is offered (TOOL-13).</summary>
    /// <exception cref="ConfigurationException">A provider does not run one of them.</exception>
    private static void ProviderToolsSupported(OfficinaOptions options, IReadOnlyDictionary<string, IModelProvider> providers)
    {
        var errors = options.Agents
            .SelectMany(agent => agent.Value.Tools.SelectMany(set => options.ToolSets[set]).Select(tool => (Tool: tool, options.Models[agent.Value.Model].Provider)))
            .Distinct()
            .Where(use => options.Tools[use.Tool].ProviderTool() is { } name
                && providers.TryGetValue(use.Provider, out var provider) && !provider.Capabilities.ProviderTools.Contains(name))
            .Select(use => new ConfigurationError(
                ValidationPhase.Provider, $"tools.{use.Tool}.source", $"provider \"{use.Provider}\" does not run the tool \"{options.Tools[use.Tool].ProviderTool()}\".",
                $"Use a tool it runs: {string.Join(", ", providers[use.Provider].Capabilities.ProviderTools.Order(StringComparer.Ordinal))}."))
            .ToList();
        if (errors.Count > 0)
        {
            throw new ConfigurationException(errors);
        }
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
            return new AgentResult(AgentOutcome.Rejected, rejection, new TurnStatistics(0, 0, Usage.None, 0m, TimeSpan.Zero), [], [], [], []);
        }

        var context = new ToolContext(work.RunId, name, work.Caller) { Masker = masker, TaskId = work.TaskId };
        var instructions = InstructionPlaceholders.Fill(agent.Instructions, Options.Project, name, agent);
        var record = new RunRecord(storage.Records, context, time);
        var turn = new Turn(
            context, Options, provider, pipeline, record, pipeline.Board(context), checks, knowledge, storage.Conversations, shortening.GetValueOrDefault(name), Events, instructions, admitted,
            Inbox(name), token => paused.TryGetValue(name, out var gate) ? gate.Task.WaitAsync(token) : Task.CompletedTask, time);
        var oneAtATime = turns.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, cancels.GetOrAdd(name, _ => new CancellationTokenSource()).Token);
        var entered = false;
        Activity? activity = null;
        Task<AgentResult>? running = null;
        AgentResult result;
        try
        {
            await oneAtATime.WaitAsync(stop.Token).ConfigureAwait(false);
            entered = true;
            activity = Telemetry.StartTurn(context); // after the wait, so the span covers the turn only
            await storage.DeleteExpiredAsync(Options.Storage.Retention, time.GetUtcNow(), stop.Token).ConfigureAwait(false);
            var started = new RunStarted(context.RunId, name, context.Caller.Id, time.GetUtcNow(), CoreVersion.Value, Options);
            await storage.Runs.RecordStartAsync(context.Caller.Tenant, started, stop.Token).ConfigureAwait(false);
            await Events.PublishAsync(context, new TurnStarted(), stop.Token).ConfigureAwait(false);
            running = turn.RunAsync(stop.Token);
            result = await running.WaitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            result = await CancelledAsync(turn, running, admitted).ConfigureAwait(false);
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

    /// <summary>
    /// RUN-06: a cancelled turn has <c>run.cancelWithin</c> to stop its model call and its tools, which stop their
    /// sandboxed processes, and ends in a handoff. One still running then is left behind, so the owner never waits longer.
    /// </summary>
    private async Task<AgentResult> CancelledAsync(Turn turn, Task<AgentResult>? running, Work work)
    {
        try
        {
            await (running ?? Task.CompletedTask).WaitAsync(Options.Run.CancelWithin, time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var detail = $"the turn was cancelled, and did not stop within {Options.Run.CancelWithin}";
            return new AgentResult(AgentOutcome.HandedOff, detail, new TurnStatistics(0, 0, Usage.None, 0m, TimeSpan.Zero), [], [], [], [],
                new Handoff(HandoffReason.RequestedByHuman, null, detail, work.Input, [], null, ""));
        }
        catch
        {
            // The turn stopped, as it should, with its cancellation.
        }

        return turn.HandOff(HandoffReason.RequestedByHuman, "the turn was cancelled");
    }
}
