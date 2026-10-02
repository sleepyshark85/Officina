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
using Sleepyshark.Officina.Core.Reports;
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
    private readonly ConcurrentDictionary<string, TaskCompletionSource> pausedRuns = new(StringComparer.Ordinal);

    // An agent of a team is its run's: its id, such as developer[2], names another agent in another run of the same team.
    private readonly ConcurrentDictionary<string, TeamState> teams = new(StringComparer.Ordinal);

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
        var (found, errors) = Shortening(options, providers, shorteners ?? new Dictionary<string, IHistoryShortener>());
        errors = [.. ModelsSupport(options, providers), .. errors];
        if (errors.Count > 0)
        {
            throw new ConfigurationException([.. errors.OrderBy(error => error.Phase)]);
        }

        gateway = new ModelGateway(options, providers, time);
        shortening = found;
        admission = new Admission(options.Policies, time);
        checkpointer = new Checkpointer(options, storage, workspace, Events, pipeline, time);
        pipeline.Messages = DeliverAsync;
        pipeline.Workspace = workspace;
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

    /// <summary>
    /// The report of a run that has ended or stopped: its outcome, work, decisions, checks, cost and open issues (RUN-11). It is
    /// built from what is stored, so the run may have ended in another process.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="caller">The run's caller; anonymous when null.</param>
    /// <param name="ct">Cancels the report.</param>
    /// <exception cref="ArgumentException">There is no such run.</exception>
    public async Task<RunReport> ReportAsync(string runId, Caller? caller = null, CancellationToken ct = default)
    {
        var (_, context) = await FindAsync(runId, caller ?? Caller.Anonymous, ct).ConfigureAwait(false);
        return (await RunReport.BuildAsync(storage, context.Caller.Tenant, runId, ct).ConfigureAwait(false))!;
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
        var (notUndone, memoryChanges) = await checkpointer.RestoreAsync(context, to, ct).ConfigureAwait(false);
        await storage.Runs.RecordStatusAsync(context.Caller.Tenant, runId, RunStatus.Running, ct).ConfigureAwait(false);
        await Events.PublishAsync(context, new RunRolledBack(number, notUndone, memoryChanges.Count), ct).ConfigureAwait(false);
        return new RollbackReport(to, notUndone, memoryChanges);
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
        var saved = await storage.Checkpoints.ReadAsync(caller.Tenant, runId, ct).ConfigureAwait(false);
        if (saved.Count > 0)
        {
            // Refused here, not in the run, which would end in a failed result.
            await checkpointer.EnsureNoOtherRunsAsync(new ToolContext(runId, started.Agent, caller), saved[^1], ct).ConfigureAwait(false);
        }

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

            var task = (await Board(work.Caller.Tenant, work.RunId).ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(task => task.Id == work.TaskId)
                ?? throw new ArgumentException($"The work is for task {work.TaskId}, which is not on the board.", nameof(work));
            if (task.State is TaskState.Done or TaskState.Cancelled or TaskState.Failed)
            {
                throw new ArgumentException($"The work is for task {work.TaskId}, which is {task.State}: nobody works on it unless it is retried.", nameof(work));
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

    /// <summary>
    /// Sends an agent of a run's team a message, as <see cref="Send(string, Sender, string)"/> does for an agent outside a team
    /// (CTX-08, HITL-03). It reaches only that agent of that run.
    /// </summary>
    /// <param name="runId">The team's run.</param>
    /// <param name="agentId">The agent, by its id in the team, such as <c>developer[2]</c>, or the lead's name.</param>
    /// <param name="from">Who sends it.</param>
    /// <param name="text">The message.</param>
    /// <exception cref="ArgumentException">The run has no team at work, or the team no such agent.</exception>
    public void Send(string runId, string agentId, Sender from, string text)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(text);
        Team(runId, agentId).Inboxes.GetOrAdd(agentId, _ => new()).Enqueue((from, text));
    }

    /// <summary>Pauses the agent's turns before their next model call, until it is resumed (RUN-06). Other agents go on.</summary>
    /// <param name="agentName">The agent, by its name in <c>agents</c>; every run of it is paused.</param>
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

    /// <summary>Pauses one agent of a run's team before its next model call, until it is resumed (RUN-06). The others go on.</summary>
    /// <param name="runId">The team's run.</param>
    /// <param name="agentId">The agent, by its id in the team, such as <c>developer[2]</c>, or the lead's name.</param>
    /// <exception cref="ArgumentException">The run has no team at work, or the team no such agent.</exception>
    public void Pause(string runId, string agentId) =>
        Team(runId, agentId).Paused.TryAdd(agentId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <inheritdoc cref="Pause(string, string)"/>
    public void Resume(string runId, string agentId)
    {
        if (Team(runId, agentId).Paused.TryRemove(agentId, out var gate))
        {
            gate.SetResult();
        }
    }

    /// <summary>
    /// Stops one agent of a run's team (RUN-06): its turn ends in a handoff within <c>run.cancelWithin</c>. An agent's task goes
    /// back to the lead (TEAM-09), and the team goes on and may give the agent other work; stopping the lead ends the team, in
    /// a handoff, as any lead turn that does not complete does.
    /// </summary>
    /// <inheritdoc cref="Pause(string, string)"/>
    public void Cancel(string runId, string agentId)
    {
        if (Team(runId, agentId).Cancels.TryRemove(agentId, out var cancel))
        {
            cancel.Cancel();
            cancel.Dispose();
        }
    }

    /// <summary>Pauses every agent of a run before its next model call, until the run is resumed (RUN-06). Other runs go on.</summary>
    public void PauseRun(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        pausedRuns.TryAdd(runId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public void ResumeRun(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (pausedRuns.TryRemove(runId, out var gate))
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
    /// provider tools it is offered (TOOL-13), the provider features switched on (CLD-06), and, for a fallback, turn-scoped
    /// messages where the profile's model has them.
    /// </summary>
    private static List<ConfigurationError> ModelsSupport(OfficinaOptions options, IReadOnlyDictionary<string, IModelProvider> providers)
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
                // CLD-06: a feature switched on for the provider must be one its model has.
                errors.AddRange(options.Providers[profile.Provider].Features.On().Where(feature => !capabilities.Features.Contains(feature)).Select(feature => new ConfigurationError(
                    ValidationPhase.Provider, $"providers.{profile.Provider}.features.{feature}",
                    $"{who}model \"{profile.Model}\" of agent \"{agentName}\" does not have the feature.", "Switch it off, or use a model that has it.")));
                if (isFallback && primaryTurnScoped && !capabilities.TurnScopedMessages)
                {
                    errors.Add(new(ValidationPhase.Provider, at, $"{who}model \"{profile.Model}\" does not take turn-scoped system messages, which the agent's model uses.",
                        "Use a fallback that does."));
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// CFG-06: what a runner over these providers and shorteners would refuse in the configuration, as it is constructed: a model
    /// that lacks a provider tool, a feature or turn-scoped messages it needs (MDL-06), and a history shortening that is not
    /// available (HIST-01). A host's <c>config validate</c> reports them without starting a run.
    /// </summary>
    public static IReadOnlyList<ConfigurationError> ProviderErrors(
        OfficinaOptions options, IReadOnlyDictionary<string, IModelProvider> providers, IReadOnlyDictionary<string, IHistoryShortener> shorteners)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(shorteners);
        return [.. ModelsSupport(options, providers).Concat(Shortening(options, providers, shorteners).Errors).OrderBy(error => error.Phase)];
    }

    /// <summary>
    /// What shortens the history of each agent whose strategy is <c>shortened</c> (HIST-01): its model provider, which
    /// must be able to, or a shortener the application registers.
    /// </summary>
    private static (Dictionary<string, IHistoryShortener> Found, List<ConfigurationError> Errors) Shortening(
        OfficinaOptions options, IReadOnlyDictionary<string, IModelProvider> providers, IReadOnlyDictionary<string, IHistoryShortener> shorteners)
    {
        var found = new Dictionary<string, IHistoryShortener>(StringComparer.Ordinal);
        var errors = new List<ConfigurationError>();
        foreach (var (name, agent) in options.Agents.Where(agent => agent.Value.Context.History.Strategy == HistoryStrategy.Shortened))
        {
            var path = $"agents.{name}.context.history.shortening";
            var provider = options.Models[agent.Model].Provider;
            var id = agent.Context.History.ExtensionId();
            var model = options.Models[agent.Model].Model;
            var summarizes = id is null && providers.GetValueOrDefault(provider) is { } own && own.CapabilitiesOf(model).Summarizes;
            if ((id is null ? (summarizes ? ProviderSummary.Instance : providers.GetValueOrDefault(provider) as IHistoryShortener) : shorteners.GetValueOrDefault(id)) is { } shortener)
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
                errors.Add(new(ValidationPhase.Provider, path, $"provider \"{provider}\" cannot shorten history with model \"{model}\".",
                    "Use extension:<id> for a shortener the application registers."));
            }
        }

        return (found, errors);
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

    /// <summary>The owner's cancellation of an agent of a run's team (RUN-06).</summary>
    private CancellationToken CancelOf(string runId, string agentId) => Team(runId, agentId).Cancels.GetOrAdd(agentId, _ => new CancellationTokenSource()).Token;

    /// <summary>The team at work in a run, which has the agent.</summary>
    /// <exception cref="ArgumentException">The run has no team at work, or the team no such agent.</exception>
    private TeamState Team(string runId, string agentId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(agentId);
        return teams.TryGetValue(runId, out var team) && team.Members.Contains(agentId)
            ? team
            : throw new ArgumentException($"Run {runId} has no team at work with an agent {agentId}.", nameof(agentId));
    }

    /// <summary>
    /// Registers who is in a run's team, or, with null, forgets the team, and with it the messages left for its agents and the
    /// owner's pauses and cancellations of them. What a late caller adds to a forgotten team reaches nobody.
    /// </summary>
    private void Members(string runId, IReadOnlySet<string>? members)
    {
        if (members is not null)
        {
            teams[runId] = new TeamState(members);
            return;
        }

        if (teams.TryRemove(runId, out var ended))
        {
            foreach (var gate in ended.Paused.Values)
            {
                gate.TrySetResult();
            }

            foreach (var cancel in ended.Cancels.Values)
            {
                cancel.Dispose();
            }
        }
    }

    /// <summary>
    /// TEAM-06: a message from an agent of a team to another of the same team and run. It is recorded, with its sender and
    /// recipient, before it is delivered, and reaches the recipient as data labelled with its sender (INV-08).
    /// </summary>
    private async ValueTask<string?> DeliverAsync(ToolContext from, string to, string text, CancellationToken ct)
    {
        if (from.Instance is null || !teams.TryGetValue(from.RunId, out var team))
        {
            return "only the agents of a team send each other messages.";
        }

        if (to == from.AgentId || !team.Members.Contains(to))
        {
            return $"{to} is not another agent of your team. Send to one of: {string.Join(", ", team.Members.Where(member => member != from.AgentId))}.";
        }

        await Events.PublishAsync(from, new MessageSent(to, text), ct).ConfigureAwait(false);
        team.Inboxes.GetOrAdd(to, _ => new()).Enqueue((new Sender(SenderKind.Agent, from.AgentId), text));
        return null;
    }

    /// <summary>Waits while the owner has paused the agent, its definition or its run (RUN-06).</summary>
    private async Task WhilePausedAsync(ToolContext context, CancellationToken ct)
    {
        while ((paused.GetValueOrDefault(context.Agent)
            ?? (context.Instance is { } instance && teams.TryGetValue(context.RunId, out var team) ? team.Paused.GetValueOrDefault(instance) : null)
            ?? pausedRuns.GetValueOrDefault(context.RunId)) is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

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

        var context = new ToolContext(work.RunId, name, work.Caller) { Masker = masker, TaskId = work.TaskId, Run = new(), Helpers = new() };
        var steps = new Steps(Options, patterns, checks, Events, NewTurn, context, admitted, time, checkpointer.TakeAsync, new(pipeline, pipeline.Workspace, storage.Events, agentId => CancelOf(context.RunId, agentId), Members));
        var oneAtATime = turns.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, cancels.GetOrAdd(name, _ => new CancellationTokenSource()).Token);
        var entered = false;
        Activity? activity = null;
        Task<StepResult>? running = null;
        var recorded = false;
        var leftBehind = false;
        var spent = (Run: default(Spent), Agent: default(Spent));
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

                // INV-07: what the run spent before it died still counts against its budgets.
                var before = await storage.Events.ReadAsync(context.Caller.Tenant, context.RunId, 0, stop.Token).ConfigureAwait(false);
                spent = Spent.Of(before);
                context.Helpers!.After(before);
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
            running = steps.RunAsync(Budget.ForRun(Options.Run.Budget, time, spent.Run).DrawAgent(agent.Budget.Total, spent.Agent), stop.Token);
            ended = await running.WaitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            (ended, leftBehind) = await CancelledAsync(steps, running).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ended = Failed(exception);
        }
        finally
        {
            if (entered && leftBehind)
            {
                // LOOP-02: a turn left behind still runs, so the agent's next turn waits until it has stopped.
                _ = running!.ContinueWith(_ => oneAtATime.Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else if (entered)
            {
                oneAtATime.Release();
            }

            // The owner's pause of the run ends with its work.
            if (pausedRuns.TryRemove(context.RunId, out var pausedRun))
            {
                pausedRun.SetResult();
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
        var interrupted = await checkpointer.InterruptedAsync(context, ct).ConfigureAwait(false);
        var saved = await storage.Checkpoints.ReadAsync(context.Caller.Tenant, context.RunId, ct).ConfigureAwait(false);
        var last = saved.Count == 0 ? null : saved[^1];
        if (last is not null)
        {
            await checkpointer.RestoreAsync(context, last, ct).ConfigureAwait(false);
        }

        // Published before the checkpoint below, so every event after the restart follows this one (INV-07: the budget's time).
        await Events.PublishAsync(context, new RunResumed(last?.Number ?? 0, interrupted), ct).ConfigureAwait(false);
        if (last is null)
        {
            // It died before its first checkpoint, so nothing was saved and nothing is restored.
            await checkpointer.TakeAsync(context, CheckpointPoint.Start, ct).ConfigureAwait(false);
        }
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
    private async Task<(StepResult Ended, bool LeftBehind)> CancelledAsync(Steps steps, Task<StepResult>? running)
    {
        try
        {
            await (running ?? Task.CompletedTask).WaitAsync(Options.Run.CancelWithin, time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return (steps.Cancelled($"the turn was cancelled, and did not stop within {Options.Run.CancelWithin}"), true);
        }
        catch
        {
            // The run stopped, as it should, with its cancellation.
        }

        return (steps.Cancelled("the turn was cancelled"), false);
    }

    /// <summary>A turn of the context's agent on the work, within the budget given; steps of a pattern are turns too.</summary>
    private Turn NewTurn(ToolContext context, Work work, Budget budget)
    {
        var agent = Options.Agents[context.Agent];
        var instructions = InstructionPlaceholders.Fill(agent.Instructions, Options.Project, context.Agent, agent);
        var name = context.Agent;
        return new Turn(
            context, Options, gateway, pipeline, new RunRecord(storage.Records, context, time), pipeline.Board(context), pipeline.Memory(context), checks, knowledge, storage.Conversations,
            shortening.GetValueOrDefault(name), Events, instructions, work,
            context.HelperDepth > 0 ? new() // a helper reads only the work it is given
                : context.Instance is { } instance ? teams[context.RunId].Inboxes.GetOrAdd(instance, _ => new()) : Inbox(name),
            token => WhilePausedAsync(context, token), budget, time,
            (helper, input, parent, token) => NewTurn(helper, work with { Input = input }, parent).RunAsync(token));
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

/// <summary>A team at work in a run: who is in it, and each agent's messages, the owner's pause and the owner's cancellation.</summary>
internal sealed record TeamState(IReadOnlySet<string> Members)
{
    public ConcurrentDictionary<string, ConcurrentQueue<(Sender From, string Text)>> Inboxes { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, TaskCompletionSource> Paused { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, CancellationTokenSource> Cancels { get; } = new(StringComparer.Ordinal);
}
