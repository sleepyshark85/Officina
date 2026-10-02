using System.Collections.Concurrent;
using System.Diagnostics;
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
        IReadOnlyDictionary<string, ILoopPattern>? patterns = null)
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

    private async Task<AgentResult> RunCoreAsync(Work work, CancellationToken ct)
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
        var steps = new Steps(Options, patterns, checks, Events, NewTurn, context, admitted, time);
        var oneAtATime = turns.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, cancels.GetOrAdd(name, _ => new CancellationTokenSource()).Token);
        var entered = false;
        Activity? activity = null;
        Task<StepResult>? running = null;
        StepResult ended;
        try
        {
            await oneAtATime.WaitAsync(stop.Token).ConfigureAwait(false);
            entered = true;
            activity = Telemetry.StartTurn(context); // after the wait, so the span covers the turn only
            await storage.DeleteExpiredAsync(Options.Storage.Retention, time.GetUtcNow(), stop.Token).ConfigureAwait(false);
            var started = new RunStarted(context.RunId, name, context.Caller.Id, time.GetUtcNow(), CoreVersion.Value, Options);
            await storage.Runs.RecordStartAsync(context.Caller.Tenant, started, stop.Token).ConfigureAwait(false);
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
