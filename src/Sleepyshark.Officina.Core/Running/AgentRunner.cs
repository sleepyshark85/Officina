using System.Collections.Concurrent;
using System.Diagnostics;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Memory;
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
    private readonly ModelGateway gateway;
    private readonly IStorage storage;
    private readonly TimeProvider time;
    private readonly ToolPipeline pipeline;
    private readonly IReadOnlyDictionary<string, ICheck> checks;
    private readonly IReadOnlyDictionary<string, IKnowledgeSource> knowledge;
    private readonly IReadOnlyDictionary<string, ILoopPattern> patterns;
    private readonly Dictionary<string, IHistoryShortener> shortening;
    private readonly Admission admission;
    private readonly Checkpointer checkpointer;
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
    /// <param name="patterns">The application's loop patterns, by the id that <c>extension:&lt;id&gt;</c> patterns name (PAT-07).</param>
    /// <param name="workspace">The workspace whose working copies checkpoints save and restore; null when the run has none (RUN-04).</param>
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
        IReadOnlyDictionary<string, IHistoryShortener>? shorteners = null,
        IReadOnlyDictionary<string, ILoopPattern>? patterns = null,
        IWorkspace? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(checks);
        this.providers = providers;
        this.storage = storage;
        this.time = time;
        this.patterns = patterns ?? new Dictionary<string, ILoopPattern>();
        Events = new EventBus(storage.Events, options.Storage, time);
        pipeline = new ToolPipeline(options, tools, gates, knowledge, checks, storage, Events, human, secrets, time);
        var unregistered = options.Agents.SelectMany(agent => agent.Value.Pattern.Nested($"agents.{agent.Key}.pattern"))
            .Where(nested => nested.Pattern.ExtensionId() is { } id && !this.patterns.ContainsKey(id))
            .Select(nested => ToolCatalog.Unregistered($"{nested.Path}.type", "pattern", nested.Pattern.ExtensionId()!))
            .ToList();
        if (unregistered.Count > 0)
        {
            throw new ConfigurationException(unregistered);
        }

        this.checks = checks;
        this.knowledge = knowledge;
        ModelsSupport(options, providers);
        gateway = new ModelGateway(options, providers, time);
        shortening = Shortening(options, providers, shorteners ?? new Dictionary<string, IHistoryShortener>());
        admission = new Admission(options.Policies, time);
        checkpointer = new Checkpointer(options, storage, workspace, Events, pipeline, time);
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

    /// <summary>
    /// The project memory of a caller's agents, as the owner changes it: adds an entry, and approves or rejects proposals,
    /// including the condensing only the owner approves (MEM-03, MEM-05).
    /// </summary>
    /// <param name="caller">Whose memory: the project's, this caller's or the tenant's, as configured (MEM-04).</param>
    /// <exception cref="InvalidOperationException">Project memory is off.</exception>
    public ProjectMemory Memory(Caller caller) => pipeline.Memory(caller);

    /// <summary>How write tool calls are decided (HITL-01). The owner may change it during a run; each agent's next call uses it.</summary>
    public PermissionMode PermissionMode
    {
        get => pipeline.PermissionMode;
        set => pipeline.PermissionMode = value;
    }

    /// <summary>
    /// Takes a checkpoint of a run now (RUN-03). The owner asks for one at any time, and a host that integrates a change asks
    /// for one at <see cref="CheckpointPoint.Integration"/>, which is taken only if the configuration lists it.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="caller">The run's caller; anonymous when null.</param>
    /// <param name="point">Why: on demand, or after an integration.</param>
    /// <param name="ct">Cancels the checkpoint.</param>
    /// <returns>The checkpoint; null when the configuration takes none at this point.</returns>
    /// <exception cref="ArgumentException">There is no such run.</exception>
    public async Task<Checkpoint?> CheckpointAsync(string runId, Caller? caller = null, CheckpointPoint point = CheckpointPoint.OnDemand, CancellationToken ct = default)
    {
        var (_, context) = await FindAsync(runId, caller ?? Caller.Anonymous, ct).ConfigureAwait(false);
        return await checkpointer.TakeAsync(context, point, ct).ConfigureAwait(false);
    }

    /// <summary>A run's checkpoints, in order (RUN-08).</summary>
    /// <exception cref="ArgumentException">There is no such run.</exception>
    public async Task<IReadOnlyList<Checkpoint>> CheckpointsAsync(string runId, Caller? caller = null, CancellationToken ct = default)
    {
        var (_, context) = await FindAsync(runId, caller ?? Caller.Anonymous, ct).ConfigureAwait(false);
        return await storage.Checkpoints.ReadAsync(context.Caller.Tenant, runId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls a run back to one of its checkpoints, restoring its state and its working copies together (RUN-08). Nothing
    /// outside the core's state is undone: the report lists the effects of write tools made since, such as commands and
    /// calls to servers. The run is then running again as far as the store knows, so <see cref="ResumeAsync"/> goes on from there.
    /// Stop the run's turns first, and drop any handle on its working copies: the host has none to keep.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="number">The checkpoint, from 0 for the run's start.</param>
    /// <param name="caller">The run's caller; anonymous when null.</param>
    /// <param name="ct">Cancels the rollback.</param>
    /// <exception cref="ArgumentException">There is no such run or checkpoint.</exception>
    /// <exception cref="InvalidOperationException">Checkpoints are off, or a turn is running.</exception>
    public async Task<RollbackReport> RollbackAsync(string runId, int number, Caller? caller = null, CancellationToken ct = default)
    {
        NeedCheckpoints();
        if (turns.Values.Any(turn => turn.CurrentCount == 0))
        {
            throw new InvalidOperationException("A turn is running. Cancel it before rolling a run back.");
        }

        var (_, context) = await FindAsync(runId, caller ?? Caller.Anonymous, ct).ConfigureAwait(false);
        var to = (await storage.Checkpoints.ReadAsync(context.Caller.Tenant, runId, ct).ConfigureAwait(false)).FirstOrDefault(checkpoint => checkpoint.Number == number)
            ?? throw new ArgumentException($"Run {runId} has no checkpoint {number}.", nameof(number));
        var notUndone = await checkpointer.RestoreAsync(context, to, ct).ConfigureAwait(false);
        await storage.Runs.RecordStatusAsync(context.Caller.Tenant, runId, RunStatus.Running, ct).ConfigureAwait(false);
        await Events.PublishAsync(context, new RunRolledBack(number, notUndone), ct).ConfigureAwait(false);
        return new RollbackReport(to, notUndone);
    }

    /// <summary>
    /// Starts a run again after a crash or a restart, from its last checkpoint (RUN-04). Its state and working copies are
    /// restored to the checkpoint first, and the work since is redone: the run's work starts again on the restored state.
    /// A write tool call whose outcome is unknown is flagged in a <see cref="RunResumed"/> event, and an irreversible one is
    /// never run again, so it goes to a human (RUN-07, TOOL-10). The run's configuration is the runner's, not the stored one.
    /// </summary>
    /// <param name="runId">The run, as it was started.</param>
    /// <param name="caller">The run's caller, with its permissions: only the caller's id and tenant are stored. Anonymous when null.</param>
    /// <param name="ct">Cancels the run, which then ends in a handoff.</param>
    /// <exception cref="ArgumentException">There is no such run for the caller.</exception>
    /// <exception cref="InvalidOperationException">Checkpoints are off, or the run has ended.</exception>
    public async Task<AgentResult> ResumeAsync(string runId, Caller? caller = null, CancellationToken ct = default)
    {
        NeedCheckpoints();
        caller ??= Caller.Anonymous;
        var (stored, _) = await FindAsync(runId, caller, ct).ConfigureAwait(false);
        if (stored.Status != RunStatus.Running)
        {
            throw new InvalidOperationException($"Run {runId} has ended ({stored.Status}), so it does not resume. Roll it back to a checkpoint to run it again.");
        }

        var started = stored.Started;
        KnownAgent(started.Agent);
        var work = new Work(started.Agent, started.Input) { Trigger = started.Trigger, Caller = caller, TaskId = started.TaskId, RunId = runId };
        return await RunCoreAsync(work, ct, resume: true).ConfigureAwait(false);
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

    /// <summary>
    /// MDL-06, MDL-04: the model of each agent's profile, and of each fallback of it, must support what the agent uses: the
    /// provider tools it is offered (TOOL-13), and, for a fallback, turn-scoped messages where the profile's model has them.
    /// </summary>
    /// <exception cref="ConfigurationException">A model does not support one of them.</exception>
    private static void ModelsSupport(OfficinaOptions options, IReadOnlyDictionary<string, IModelProvider> providers)
    {
        var errors = new List<ConfigurationError>();
        foreach (var (agentName, agent) in options.Agents)
        {
            var primary = options.Models[agent.Model];
            var used = agent.Tools.SelectMany(set => options.ToolSets[set]).Distinct().Where(tool => options.Tools[tool].ProviderTool() is not null).ToList();
            var primaryTurnScoped = providers.TryGetValue(primary.Provider, out var primaryProvider) && primaryProvider.CapabilitiesOf(primary.Model).TurnScopedMessages;
            foreach (var (name, profile) in primary.Fallbacks.Select(name => (name, options.Models[name])).Prepend((agent.Model, primary)))
            {
                var isFallback = name != agent.Model;
                var at = $"models.{agent.Model}.fallbacks";
                var who = isFallback ? $"fallback \"{name}\" of agent \"{agentName}\": " : "";
                if (!providers.TryGetValue(profile.Provider, out var provider))
                {
                    if (isFallback)
                    {
                        errors.Add(new(ValidationPhase.Provider, at, $"{who}provider \"{profile.Provider}\" has no implementation registered.", ""));
                    }

                    continue;
                }

                var capabilities = provider.CapabilitiesOf(profile.Model);
                errors.AddRange(used.Where(tool => !capabilities.ProviderTools.Contains(options.Tools[tool].ProviderTool()!)).Select(tool => new ConfigurationError(
                    ValidationPhase.Provider, isFallback ? at : $"tools.{tool}.source",
                    $"{who}provider \"{profile.Provider}\" does not run the tool \"{options.Tools[tool].ProviderTool()}\"{(isFallback ? $" with model \"{profile.Model}\"" : "")}.",
                    $"Use a tool it runs: {string.Join(", ", capabilities.ProviderTools.Order(StringComparer.Ordinal))}.")));
                if (isFallback && primaryTurnScoped && !capabilities.TurnScopedMessages)
                {
                    errors.Add(new(ValidationPhase.Provider, at, $"{who}model \"{profile.Model}\" does not take turn-scoped system messages, which the agent's model uses.",
                        "Use a fallback that does."));
                }
            }
        }

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

    private void NeedCheckpoints()
    {
        if (!Options.Capabilities.Checkpoints.Enabled)
        {
            throw new InvalidOperationException("Checkpoints are off. Set capabilities.checkpoints.enabled to true.");
        }
    }

    /// <summary>The stored run of a caller, and the context that acts on it as the owner.</summary>
    private async Task<(StoredRun Run, ToolContext Context)> FindAsync(string runId, Caller caller, CancellationToken ct)
    {
        var stored = await storage.Runs.ReadAsync(caller.Tenant, runId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"There is no run {runId}.", nameof(runId));
        return stored.Started.Owner == caller.Id
            ? (stored, new ToolContext(runId, stored.Started.Agent, caller))
            : throw new ArgumentException($"There is no run {runId}.", nameof(runId));
    }

    private async Task<AgentResult> RunCoreAsync(Work work, CancellationToken ct, bool resume = false)
    {
        var name = work.Agent;
        var agent = Options.Agents[name];
        Provider(name);
        var (admitted, masker, rejection) = admission.Admit(work, agent);
        if (rejection is not null)
        {
            return new AgentResult(AgentOutcome.Rejected, rejection, new TurnStatistics(0, 0, Usage.None, 0m, TimeSpan.Zero), [], [], [], []);
        }

        var context = new ToolContext(work.RunId, name, work.Caller) { Masker = masker, TaskId = work.TaskId };
        var steps = new Steps(Options, patterns, checks, Events, NewTurn, context, admitted, time, checkpointer.TakeAsync);
        var oneAtATime = turns.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, cancels.GetOrAdd(name, _ => new CancellationTokenSource()).Token);
        var entered = false;
        Activity? activity = null;
        Task<StepResult>? running = null;
        var recorded = false;
        StepResult ended;
        try
        {
            await oneAtATime.WaitAsync(stop.Token).ConfigureAwait(false);
            entered = true;
            activity = Telemetry.StartTurn(context); // after the wait, so the span covers the turn only
            await storage.DeleteExpiredAsync(Options.Storage.Retention, time.GetUtcNow(), stop.Token).ConfigureAwait(false);
            if (resume)
            {
                await ResumeStateAsync(context, stop.Token).ConfigureAwait(false);
            }
            else
            {
                var started = new RunStarted(context.RunId, name, context.Caller.Id, time.GetUtcNow(), CoreVersion.Value, Options)
                {
                    Input = admitted.Input, Trigger = admitted.Trigger, TaskId = admitted.TaskId,
                };
                await storage.Runs.RecordStartAsync(context.Caller.Tenant, started, stop.Token).ConfigureAwait(false);
                await checkpointer.TakeAsync(context, CheckpointPoint.Start, stop.Token).ConfigureAwait(false);
            }

            recorded = true;
            await Events.PublishAsync(context, new TurnStarted(), stop.Token).ConfigureAwait(false);
            running = steps.RunAsync(Budget.ForRun(Options.Run.Budget, time), stop.Token);
            ended = await running.WaitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            ended = await CancelledAsync(steps, running).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ended = Failed(exception);
        }
        finally
        {
            if (entered)
            {
                oneAtATime.Release();
            }
        }

        var result = steps.Result(ended);
        try
        {
            // Published even when the turn was cancelled, so readers see every turn end.
            await Events.PublishAsync(context, new TurnEnded(result.Outcome, result.Handoff?.Reason), CancellationToken.None).ConfigureAwait(false);
            if (recorded)
            {
                await storage.Runs.RecordStatusAsync(context.Caller.Tenant, context.RunId, StatusOf(ended.Outcome), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            result = steps.Result(Failed(exception));
        }

        var reason = result.Handoff?.Reason.ToString() ?? (result.Outcome == AgentOutcome.Failed ? result.Output : "");
        Telemetry.TurnEnded(activity, context, result.Outcome.ToString(), result.Handoff?.Reason.ToString());
        OfficinaLog.Log.TurnEnded(context.RunId, name, result.Outcome.ToString(), reason);
        activity?.Dispose();
        return result;
    }

    /// <summary>
    /// RUN-04: the run starts again where its last checkpoint left it. Events continue the stored numbering, the state and the
    /// working copies return to the checkpoint, and the calls whose outcome is unknown are flagged (RUN-07).
    /// </summary>
    private async Task ResumeStateAsync(ToolContext context, CancellationToken ct)
    {
        await Events.ContinueAsync(context.Caller.Tenant, context.RunId, ct).ConfigureAwait(false);
        var interrupted = await checkpointer.InterruptedAsync(context, ct).ConfigureAwait(false);
        var saved = await storage.Checkpoints.ReadAsync(context.Caller.Tenant, context.RunId, ct).ConfigureAwait(false);
        var last = saved.Count == 0 ? null : saved[^1];
        if (last is null)
        {
            // It died before its first checkpoint, so nothing was saved and nothing is restored.
            last = await checkpointer.TakeAsync(context, CheckpointPoint.Start, ct).ConfigureAwait(false);
        }
        else
        {
            await checkpointer.RestoreAsync(context, last, ct).ConfigureAwait(false);
        }

        await Events.PublishAsync(context, new RunResumed(last!.Number, interrupted), ct).ConfigureAwait(false);
    }

    /// <summary>A run that ends in a handoff waits for a human (RUN-01).</summary>
    private static RunStatus StatusOf(StepOutcome outcome) => outcome switch
    {
        StepOutcome.Completed => RunStatus.Completed,
        StepOutcome.Failed => RunStatus.Failed,
        StepOutcome.Cancelled => RunStatus.Cancelled,
        _ => RunStatus.WaitingForHuman,
    };

    /// <summary>
    /// RUN-06: a cancelled run has <c>run.cancelWithin</c> to stop its model call and its tools, which stop their
    /// sandboxed processes, and ends in a handoff. One still running then is left behind, so the owner never waits longer.
    /// </summary>
    private async Task<StepResult> CancelledAsync(Steps steps, Task<StepResult>? running)
    {
        try
        {
            await (running ?? Task.CompletedTask).WaitAsync(Options.Run.CancelWithin, time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return steps.Cancelled($"the turn was cancelled, and did not stop within {Options.Run.CancelWithin}");
        }
        catch
        {
            // The run stopped, as it should, with its cancellation.
        }

        return steps.Cancelled("the turn was cancelled");
    }

    /// <summary>A turn of the context's agent on the work, within the budget given; steps of a pattern are turns too.</summary>
    private Turn NewTurn(ToolContext context, Work work, Budget budget)
    {
        var agent = Options.Agents[context.Agent];
        var instructions = InstructionPlaceholders.Fill(agent.Instructions, Options.Project, context.Agent, agent);
        var name = context.Agent;
        return new Turn(
            context, Options, gateway, pipeline, new RunRecord(storage.Records, context, time), pipeline.Board(context), pipeline.Memory(context), checks, knowledge, storage.Conversations,
            shortening.GetValueOrDefault(name), Events, instructions, work, Inbox(name),
            token => paused.TryGetValue(name, out var gate) ? gate.Task.WaitAsync(token) : Task.CompletedTask, budget, time);
    }

    private IModelProvider Provider(string agentName)
    {
        var profile = Options.Models[Options.Agents[agentName].Model];
        return providers.TryGetValue(profile.Provider, out var provider)
            ? provider
            : throw new InvalidOperationException($"Agent \"{agentName}\" uses provider \"{profile.Provider}\", which has no implementation registered.");
    }

    /// <summary>Ends the run after an exception outside its turns (REL-02).</summary>
    private static StepResult Failed(Exception exception) => new(StepOutcome.Failed, $"the turn failed: {exception.GetType().Name}");
}
