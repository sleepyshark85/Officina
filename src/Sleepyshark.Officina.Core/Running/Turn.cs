using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Memory;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Observability;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Tasks;
using Sleepyshark.Officina.Core.Tools;
using OutputFormat = Sleepyshark.Officina.Core.Configuration.OutputFormat;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// One turn of one agent, the primitive every pattern is built from (REQUIREMENTS.md §4.4). It calls the model, decides
/// the next step by the stop reason alone (LOOP-03, INV-01), runs the tools the model asks for through the tool
/// pipeline, and repeats until a stop condition holds (LOOP-05) or the turn must be handed off. Its budget, and those it
/// is drawn from, are checked before every model call (LOOP-06, PAT-06), and the turn always ends in a result (INV-07). Each call's input is built by the
/// turn's <see cref="Conversation"/>, with the messages that arrived since the last call (CTX-08). The conversation
/// starts from the history its strategy keeps (CTX-06), and the turn is stored with it when it ends (CAP-05). The turn
/// completes only with output that passes its schema, citation rule and checks (OUT, INV-09).
/// </summary>
internal sealed class Turn
{
    /// <summary>The kinds of record entry the volatile context holds by default, as CTX-01 lists them; citations are opt-in.</summary>
    private static readonly string[] DefaultKinds = ["fact", "finding", "decision"];

    private readonly ToolContext context;
    private readonly ProjectOptions project;
    private readonly AgentDefinition agent;
    private readonly Budget budget;
    private readonly RunBudget runBudget;
    private readonly double cacheHitWarning;
    private readonly Conversation conversation;
    private readonly ConcurrentQueue<(Sender From, string Text)> inbox;
    private readonly IModelProvider provider;
    private readonly ModelPrice? price;
    private readonly ToolPipeline tools;
    private readonly RunRecord record;
    private readonly TaskBoard? board;
    private readonly ProjectMemory? memory;
    private readonly JsonSchema? outputSchema;
    private readonly CitationRule citations;
    private readonly IReadOnlyList<(string Name, ICheck Check)> checks;
    private readonly IReadOnlyList<(string Name, IKnowledgeSource Source, bool Mask)> beforeTurn;
    private readonly IConversationStore conversations;
    private readonly IHistoryShortener? shortener;
    private readonly EventBus events;
    private readonly TimeProvider time;
    private readonly string work;
    private readonly bool batch;
    private readonly bool handOffToHuman;
    private readonly bool signOffToExceedRunBudget;
    private readonly Func<CancellationToken, Task> whilePaused;
    private readonly List<ToolAttempt> attempts = [];
    private readonly List<CacheWarning> cacheWarnings = [];
    private readonly List<string> retrieved = [];
    private readonly List<Artifact> artifacts = [];
    private IReadOnlyList<RecordEntry> entries = [];
    private BoardTask? task;
    private long prefixMemory;
    private long seenMemory;
    private List<ToolUseContent> pending = [];
    private long? started;
    private int iterations;
    private int toolCalls;
    private int withoutProgress;
    private int outputAttempts;
    private Usage usage = Usage.None;
    private decimal cost;
    private string lastText = "";

    /// <param name="context">Who the turn's tool calls are made by.</param>
    /// <param name="options">The configuration the run uses.</param>
    /// <param name="provider">The provider of the agent's model profile.</param>
    /// <param name="tools">The tool pipeline built from <paramref name="options"/>.</param>
    /// <param name="record">The run record, which the volatile context and the citation rule read.</param>
    /// <param name="board">The task board, which holds the work's task; null when it is off.</param>
    /// <param name="memory">Project memory, which the prefix holds and changes reach the conversation from; null when it is off.</param>
    /// <param name="checks">The application's checks, by extension id.</param>
    /// <param name="knowledge">The application's knowledge sources, by extension id.</param>
    /// <param name="conversations">Where the agent's conversation is kept, when its history strategy keeps one.</param>
    /// <param name="shortener">What shortens the history when the model reports it too long; null when it is not shortened.</param>
    /// <param name="events">Where model text and model calls are published.</param>
    /// <param name="instructions">The agent's instructions, placeholders filled.</param>
    /// <param name="work">What the turn is asked to do, as admission passed it.</param>
    /// <param name="inbox">Messages sent to the agent, which the turn adds to its history before each model call, masked like the work.</param>
    /// <param name="whilePaused">Waits while the owner has paused the agent (RUN-06).</param>
    /// <param name="budget">The budget the turn's own is drawn from: its pattern's, or the run's.</param>
    /// <param name="time">The clock for the time budgets and the operating facts.</param>
    public Turn(
        ToolContext context,
        OfficinaOptions options,
        IModelProvider provider,
        ToolPipeline tools,
        RunRecord record,
        TaskBoard? board,
        ProjectMemory? memory,
        IReadOnlyDictionary<string, ICheck> checks,
        IReadOnlyDictionary<string, IKnowledgeSource> knowledge,
        IConversationStore conversations,
        IHistoryShortener? shortener,
        EventBus events,
        string instructions,
        Work work,
        ConcurrentQueue<(Sender From, string Text)> inbox,
        Func<CancellationToken, Task> whilePaused,
        Budget budget,
        TimeProvider time)
    {
        this.context = context;
        project = options.Project;
        agent = options.Agents[context.Agent];
        this.budget = budget.Draw("turn's", agent.Budget.Turn);
        runBudget = options.Run.Budget;
        cacheHitWarning = options.Operations.Telemetry.CacheHitWarning;
        var profile = options.Models[agent.Model];
        price = options.Providers[profile.Provider].Prices.GetValueOrDefault(profile.Model);
        conversation = new Conversation(profile, [.. tools.Offered(context.Agent)], instructions, agent.Context, provider.Capabilities);
        this.inbox = inbox;
        this.provider = provider;
        this.tools = tools;
        this.record = record;
        this.board = board;
        this.memory = memory;
        outputSchema = agent.Output.Format == OutputFormat.Structured ? JsonSchema.FromText(agent.Output.Schema!) : null;
        citations = agent.Output.Citations ?? (options.Capabilities.Knowledge.Enabled ? CitationRule.Resolve : CitationRule.Off);
        this.checks = [.. agent.Output.Checks.Select(name => (name, checks[options.Checks[name].ExtensionId()!]))];
        beforeTurn = [.. agent.Context.Retrieval.BeforeTurn.Select(name => (name, knowledge[options.Knowledge[name].ExtensionId()!], options.Knowledge[name].Mask))];
        this.conversations = conversations;
        this.shortener = shortener;
        this.events = events;
        this.time = time;
        this.work = work.Input;
        batch = work.Trigger == Trigger.Batch;
        handOffToHuman = work.HandOffToHuman;
        signOffToExceedRunBudget = options.Capabilities.HumanInteraction.SignsOff(SignOff.RunBudgetExceeded);
        this.whilePaused = whilePaused;
    }

    public async Task<AgentResult> RunAsync(CancellationToken ct)
    {
        started = time.GetTimestamp();
        var history = agent.Context.History;
        IReadOnlyList<ConversationTurn> earlier = [];
        if (history.Strategy != HistoryStrategy.None)
        {
            earlier = await conversations.ReadAsync(context.Caller.Tenant, context.Agent, context.Caller.Id, ct).ConfigureAwait(false);
            conversation.Continue((history.Strategy == HistoryStrategy.LastTurns ? earlier.TakeLast(history.LastTurns) : earlier).SelectMany(turn => turn.Messages));
        }

        // A window of the last turns is not append-only, so its prefix starts afresh each turn.
        await StartMemoryAsync(history.Strategy == HistoryStrategy.LastTurns ? null : (earlier.Count > 0 ? earlier[^1] : null), ct).ConfigureAwait(false);
        conversation.Add(Message.User(work));
        var result = await RunTurnAsync(ct).ConfigureAwait(false);
        if (task is not null && cost > 0)
        {
            await board!.ChargeAsync(cost, ct).ConfigureAwait(false); // TASK-09
        }

        if (history.Strategy != HistoryStrategy.None)
        {
            var messages = conversation.Shortened ? conversation.History : conversation.CurrentTurn;
            var turn = new ConversationTurn(context.Agent, context.Caller.Id, time.GetUtcNow(), messages, conversation.Shortened, prefixMemory, seenMemory);
            await conversations.AppendAsync(context.Caller.Tenant, turn, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<AgentResult> RunTurnAsync(CancellationToken ct)
    {
        if (handOffToHuman)
        {
            return HandOff(HandoffReason.RequestedByHuman, "the request asks for a human", ToolResult.Human); // EGR-04
        }

        if (await RetrieveAsync(ct).ConfigureAwait(false) is { } notCovered)
        {
            return notCovered;
        }

        await ReadStateAsync(ct).ConfigureAwait(false);

        while (true)
        {
            await whilePaused(ct).ConfigureAwait(false);
            if (Exhausted() is { } exhausted && !(exhausted.OfRun && await SignedOffToExceedAsync(exhausted.Limit, ct).ConfigureAwait(false)))
            {
                return HandOff(HandoffReason.BudgetExhausted, $"the {exhausted.Limit} budget is used up");
            }

            iterations++;
            budget.Spend(iterations: 1);
            while (inbox.TryDequeue(out var message))
            {
                conversation.Add(Labels.From(message.From, Mask(message.Text)));
            }

            await AnnounceMemoryAsync(ct).ConfigureAwait(false);
            var request = conversation.Next(Facts()) with { Batch = batch };
            StopReason stop;
            try
            {
                stop = await CallModelAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The exception's message may quote the request, so only its type or classification is reported (MDL-05).
                return HandOff(HandoffReason.ProviderFailure,
                    $"the model call failed: {(exception is ModelCallException failed ? failed.Failure.ToString() : exception.GetType().Name)}");
            }

            var result = stop switch
            {
                StopReason.WantsTools => await RunToolsAsync(ct).ConfigureAwait(false),
                StopReason.Finished or StopReason.StopSequence => agent.StopWhen.Finished || agent.StopWhen.ChecksPass
                    ? await FinishAsync(lastText, ct).ConfigureAwait(false)
                    : HandOff(HandoffReason.NoProgress, "the model finished, but no stop condition holds"),
                StopReason.Paused => null,
                StopReason.OutputLimit => HandOff(HandoffReason.TruncatedOutput, "the reply reached the output limit"),
                StopReason.Refused => HandOff(HandoffReason.ProviderRefusal, "the model refused"),
                StopReason.InputTooLong => await ShortenAsync(request, ct).ConfigureAwait(false),
                _ => HandOff(HandoffReason.ProviderFailure, "the model stopped for an unknown reason"),
            };
            if (result is not null)
            {
                return result;
            }

            if (iterations >= agent.StopWhen.MaxIterations)
            {
                // Not revisable, so never null.
                return (await FinishAsync(lastText, ct, revisable: false).ConfigureAwait(false))!;
            }
        }
    }

    public AgentResult HandOff(HandoffReason reason, string detail, string? to = null, ToolRequest? pendingAction = null) =>
        End(AgentOutcome.HandedOff, detail, () => new Handoff(reason, to, detail, work, [.. attempts], pendingAction, lastText));

    /// <summary>Ends the turn after an exception the turn could not turn into a handoff (REL-02).</summary>
    public AgentResult Fail(Exception exception) => End(AgentOutcome.Failed, $"the turn failed: {exception.GetType().Name}");

    private AgentResult Complete(string output) => End(AgentOutcome.Completed, output);

    /// <summary>Ends the turn. The handoff, if any, is built once every tool request has its result, so it lists them all.</summary>
    private AgentResult End(AgentOutcome outcome, string output, Func<Handoff>? handoff = null)
    {
        CancelPending();
        var statistics = new TurnStatistics(iterations, toolCalls, usage, cost, Elapsed);
        return new AgentResult(outcome, output, statistics, conversation.History, [.. cacheWarnings], [.. entries], [.. artifacts], handoff?.Invoke());
    }

    /// <summary>Time since the turn started; zero for a turn cancelled before it started.</summary>
    private TimeSpan Elapsed => started is { } at ? time.GetElapsedTime(at) : TimeSpan.Zero;

    /// <summary>
    /// Searches the knowledge sources configured before the turn with its work, once, and keeps what each found as a
    /// labelled fact of the volatile context (CTX-04), and its passages as citations in the run record. When none covers the work, the turn can end in a handoff for a
    /// policy gap (CTX-05).
    /// </summary>
    private async Task<AgentResult?> RetrieveAsync(CancellationToken ct)
    {
        var covered = false;
        foreach (var (name, source, mask) in beforeTurn)
        {
            var retrieval = await source.RetrieveAsync(new RetrievalQuery(work, context.Caller, KnowledgeTool.MaxPassages), ct).ConfigureAwait(false);
            covered |= retrieval.Coverage != Coverage.NotCovered;
            await KnowledgeTool.CiteAsync(record, name, retrieval, mask, ct).ConfigureAwait(false);
            var passages = KnowledgeTool.Format(retrieval);
            retrieved.Add(Labels.Data($"knowledge:{name}", mask ? Mask(passages) : passages));
        }

        return beforeTurn.Count > 0 && !covered && agent.Context.Retrieval.HandOffWhenNotCovered
            ? HandOff(HandoffReason.PolicyGap, "the knowledge sources do not cover the work")
            : null;
    }

    /// <summary>
    /// HIST-04: when the model reports the input too long, the history is shortened once, and the call is made again. If
    /// it cannot be, or the input is still too long, the turn ends in a handoff.
    /// </summary>
    private async Task<AgentResult?> ShortenAsync(ModelRequest request, CancellationToken ct)
    {
        if (shortener is null || conversation.Shortened)
        {
            return HandOff(HandoffReason.ProviderFailure, "the input is too long for the model");
        }

        ImmutableArray<Message> shortened;
        try
        {
            shortened = await shortener.ShortenAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The exception's message may quote the history, so only its type is reported, as for a failed model call.
            return HandOff(HandoffReason.ProviderFailure, $"the input is too long for the model, and shortening it failed: {exception.GetType().Name}");
        }

        if (conversation.Shorten(shortened) is { } problem)
        {
            return HandOff(HandoffReason.ProviderFailure, $"the input is too long for the model, and the shortened history {problem}");
        }

        // The shortened history may have lost the messages that told of memory changes, so they are told again.
        seenMemory = prefixMemory;
        return null;
    }

    /// <summary>
    /// Puts project memory into the prefix, as of the revision the conversation started with (MEM-01). A conversation
    /// that continues keeps that revision, so its prefix is never edited; a new one starts with the memory as it is now (MEM-03).
    /// </summary>
    private async Task StartMemoryAsync(ConversationTurn? last, CancellationToken ct)
    {
        if (memory is null)
        {
            return;
        }

        var state = await memory.ReadAsync(ct).ConfigureAwait(false);
        (prefixMemory, seenMemory) = last is null ? (state.Revision, state.Revision) : (last.PrefixMemory, last.SeenMemory);
        conversation.UseMemory(state.Text(prefixMemory));
    }

    /// <summary>MEM-03: changes approved since the conversation was last told reach it as an appended operator message, before the next call.</summary>
    private async Task AnnounceMemoryAsync(CancellationToken ct)
    {
        if (memory is null)
        {
            return;
        }

        var state = await memory.ReadAsync(ct).ConfigureAwait(false);
        if (state.Revision > seenMemory)
        {
            conversation.Add(Labels.From(Sender.Operator, $"Project memory changed:\n{ProjectMemory.Lines(state.Entries.Where(entry => entry.Revision > seenMemory))}"));
            seenMemory = state.Revision;
        }
    }

    /// <summary>Reads the run record and the work's task, which the volatile context and the budget read.</summary>
    private async Task ReadStateAsync(CancellationToken ct)
    {
        entries = await record.ReadAsync(ct).ConfigureAwait(false);
        task = board?.TaskId is { } id ? (await board.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(task => task.Id == id) : null;
    }

    /// <summary>Which limit of the turn's, its pattern's, its task's or the run's budget is used up, if any (LOOP-06, COST-02).</summary>
    private (string Limit, bool OfRun)? Exhausted() =>
        task is not null && task.Spent + cost >= task.Budget ? ("task's cost", false) : budget.Exhausted();

    /// <summary>
    /// RUN-05, HITL-04: with the sign-off on, the owner may let the run go on past its budget, by another budget of the
    /// same size each time, so the run is never without a limit (INV-07).
    /// </summary>
    private async Task<bool> SignedOffToExceedAsync(string limit, CancellationToken ct)
    {
        if (!signOffToExceedRunBudget)
        {
            return false;
        }

        var summary = $"The {limit} budget is used up. Go on for another {runBudget.Cost} USD and {runBudget.Time}?";
        var answer = await tools.Owner.AskAsync(context, tools.Owner.Request(context, HumanRequestKind.SignOff, summary), ct).ConfigureAwait(false);
        if (answer is not { Approved: true })
        {
            return false;
        }

        budget.ExtendRun();
        return true;
    }

    /// <summary>
    /// The volatile context for this call, in the order of CTX-01: the record's facts, the passages retrieved before the
    /// turn, the record's other entries, the work's task, then the agent's operating facts, filled for this call (CTX-09).
    /// Each part of the record is in revision order, so it only grows (CTX-07). The agent sees the kinds of entry it is
    /// configured to, and only its task's when its scope is the task (REC-06).
    /// </summary>
    private List<string> Facts()
    {
        var now = time.GetUtcNow();
        var kinds = agent.Context.Record ?? DefaultKinds;
        var shown = entries.Where(entry => kinds.Contains(entry.Item.Kind) && (agent.Context.RecordScope == RecordScope.All || entry.Task == context.TaskId)).ToList();
        return [.. Record(shown.Where(entry => entry.Item is Fact)), .. retrieved, .. Record(shown.Where(entry => entry.Item is not Fact)),
            .. task is not null && agent.Context.CurrentTask ? [Labels.Data("task", TaskBoard.Describe(task))] : Array.Empty<string>(),
            .. agent.Context.OperatingFacts.Select(fact => InstructionPlaceholders.Fill(fact, project, context.Agent, agent, now, context.Caller, context.RunId, task))];

        // Entries come from tools and documents, so they are labelled as data (INV-08).
        IEnumerable<string> Record(IEnumerable<RecordEntry> part) =>
            part.Any() ? [Labels.Data("record", string.Join('\n', part.Select(entry => RunRecord.Describe(entry, entries))))] : [];
    }

    /// <summary>
    /// Completes the turn with the output if it passes (OUT-01 to OUT-04). Structured output that does not match its
    /// schema goes back to the model with the errors while attempts are left and the model is called again
    /// (<paramref name="revisable"/>), and null is returned so the turn goes on (OUT-02); so does output that fails a
    /// check when failures are revised (OUT-03). Any other failure hands the turn off.
    /// </summary>
    private async Task<AgentResult?> FinishAsync(string output, CancellationToken ct, bool revisable = true)
    {
        if (await OutputProblemAsync(output, ct).ConfigureAwait(false) is not { } found)
        {
            return Complete(output);
        }

        var (reason, problem) = found;
        var invalid = reason == HandoffReason.InvalidStructuredOutput;
        if ((invalid || agent.Output.OnCheckFailure == CheckFailure.Revise) && revisable && outputAttempts < agent.Output.Attempts)
        {
            outputAttempts++;

            // A check sees the artifacts as tools produced them, so its findings are masked before the model sees them (ING-02).
            conversation.Add(Message.User(invalid
                ? $"The output does not match its schema: {problem}. Reply again with output that does."
                : $"The output fails its checks: {Mask(problem)}. Revise it, and reply again with the whole output."));
            return null;
        }

        return HandOff(reason, problem);
    }

    /// <summary>
    /// The first problem of an output, or null when it passes: its schema (OUT-01), the citation rule (OUT-04), then the
    /// checks in order, where the first that fails decides (OUT-03). Only these decide whether output is accepted (INV-09).
    /// </summary>
    private async Task<(HandoffReason, string)?> OutputProblemAsync(string output, CancellationToken ct)
    {
        if (outputSchema is not null && SchemaProblem(output) is { } invalid)
        {
            return (HandoffReason.InvalidStructuredOutput, invalid);
        }

        if (citations != CitationRule.Off && RunRecord.Unresolved(output, entries) is { } unresolved)
        {
            return (HandoffReason.OutputCheckFailed, $"the output cites {unresolved}, which is not a citation in the run record");
        }

        if (citations == CitationRule.Required && RunRecord.Cited(output).Count == 0)
        {
            return (HandoffReason.OutputCheckFailed, "the output cites no source");
        }

        return await FailedCheckAsync(context, checks, output, artifacts, ct).ConfigureAwait(false) is { } failed
            ? (HandoffReason.OutputCheckFailed, failed)
            : null;
    }

    /// <summary>Runs the checks on the output in order, and says which failed first and what it found; null when all pass (OUT-03).</summary>
    internal static async Task<string?> FailedCheckAsync(
        ToolContext context, IEnumerable<(string Name, ICheck Check)> checks, string output, IReadOnlyList<Artifact> artifacts, CancellationToken ct)
    {
        foreach (var (name, check) in checks)
        {
            var result = await check.RunAsync(new CheckContext(null, output, [.. artifacts]), ct).ConfigureAwait(false);
            Telemetry.CheckEnded(context, name, result.Passed);
            if (!result.Passed)
            {
                return $"check {name} failed: {string.Join("; ", result.Findings)}";
            }
        }

        return null;
    }

    /// <summary>Why structured output does not match its schema, or null when it does.</summary>
    private string? SchemaProblem(string output)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return "it is not JSON";
        }

        using (document)
        {
            var result = outputSchema!.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
            return result.IsValid
                ? null
                : string.Join("; ", (result.Details ?? []).Where(detail => detail.Errors is not null)
                    .SelectMany(detail => detail.Errors!.Select(error => $"{(detail.InstanceLocation.ToString() is { Length: > 0 } at ? at : "/")}: {error.Value}")));
        }
    }

    /// <summary>Masks text from outside the core when masking is on (ING-02).</summary>
    private string Mask(string text) => context.Masker?.Mask(text) ?? text;

    /// <summary>
    /// Calls the model, and adds the reply to the conversation. Text arrives in pieces as it is generated, and is
    /// published as it arrives; the turn acts only once the reply is complete (MDL-07).
    /// </summary>
    private async Task<StopReason> CallModelAsync(ModelRequest request, CancellationToken ct)
    {
        using var activity = Telemetry.StartModelCall(context, request.Profile.Provider, request.Profile.Model);
        var callStarted = time.GetTimestamp();
        var callUsage = Usage.None;
        var callCost = 0m;
        var stop = StopReason.Unknown;
        var reply = new List<Content>();
        var text = new StringBuilder();
        await foreach (var modelEvent in provider.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            switch (modelEvent)
            {
                case TextDelta delta:
                    text.Append(delta.Text);
                    await events.PublishAsync(context, new TextGenerated(delta.Text), ct).ConfigureAwait(false);
                    break;
                case ContentReceived received:
                    AddText();
                    reply.Add(received.Content);
                    break;
                case ProviderToolUsed used:
                    // TOOL-13: the provider ran it, so it is audited after the fact and counts towards the budget.
                    toolCalls++;
                    budget.Spend(toolCalls: 1);
                    await tools.AuditProviderToolAsync(context, used.Request, used.Result, ct).ConfigureAwait(false);
                    break;
                case UsageReported reported:
                    var spent = price?.Cost(reported.Usage) ?? 0m;
                    (callUsage, callCost) = (callUsage + reported.Usage, callCost + spent);
                    (usage, cost) = (usage + reported.Usage, cost + spent);
                    budget.Spend(tokens: reported.Usage.Total, cost: spent);
                    break;
                case Stopped stopped:
                    stop = stopped.Reason;
                    break;
            }
        }

        Telemetry.ModelCallEnded(activity, context, request.Profile.Provider, request.Profile.Model, stop, callUsage, callCost, time.GetElapsedTime(callStarted));
        await events.PublishAsync(context, new ModelCallEnded(stop, callUsage, callCost), ct).ConfigureAwait(false);
        AddText();
        if (CheckCacheHits(callUsage) is { } warning)
        {
            await events.PublishAsync(context, new CacheHitWarning(warning), ct).ConfigureAwait(false);
        }

        if (reply.Count > 0)
        {
            conversation.Add(new Message(Role.Assistant, reply));
            pending = [.. reply.OfType<ToolUseContent>()];
            if (reply.OfType<TextContent>().Any())
            {
                lastText = string.Concat(reply.OfType<TextContent>().Select(piece => piece.Text));
            }
        }

        return stop;

        void AddText()
        {
            if (text.Length > 0)
            {
                reply.Add(new TextContent(text.ToString()));
                text.Clear();
            }
        }
    }

    /// <summary>
    /// COST-01: every call after the first of a turn should read from the cache all that the previous call sent, so a
    /// low share read from it means the cache is broken. The first call may have to write the conversation's cache.
    /// </summary>
    private CacheWarning? CheckCacheHits(Usage call)
    {
        var input = call.Input + call.CacheRead + call.CacheWrite;
        if (iterations > 1 && input > 0 && (double)call.CacheRead / input is var rate && rate < cacheHitWarning)
        {
            cacheWarnings.Add(new CacheWarning(iterations, rate));
            return cacheWarnings[^1];
        }

        return null;
    }

    /// <summary>Runs the calls of the last reply, and decides whether the turn ends with them; null when it goes on.</summary>
    private async Task<AgentResult?> RunToolsAsync(CancellationToken ct)
    {
        if (pending.Count == 0)
        {
            return HandOff(HandoffReason.ProviderFailure, "the model asked for tools but requested none"); // LOOP-04
        }

        var requests = pending.Select(call => new ToolRequest(call.Name, call.Arguments)).ToList();
        var results = await tools.RunAsync(context, requests, ct).ConfigureAwait(false);
        artifacts.AddRange(results.SelectMany(result => result.Artifacts));
        await ReadStateAsync(ct).ConfigureAwait(false);
        conversation.Add(new Message(
            Role.User, pending.Zip(results, (call, result) => new ToolResultContent(call.Id, Labels.Data($"tool:{call.Name}", result.Content), result.Error is not null))));
        pending = [];
        toolCalls += requests.Count;
        budget.Spend(toolCalls: requests.Count);
        var batch = requests.Zip(results, (call, result) => new ToolAttempt(call, result)).ToList();
        var progressed = batch.Any(IsProgress);
        attempts.AddRange(batch);

        if (batch.FirstOrDefault(attempt => attempt.Result.RouteTo is not null) is { } routed)
        {
            return HandOff(HandoffReason.RoutedByGate, routed.Result.Content, routed.Result.RouteTo, routed.Request);
        }

        if (batch.FirstOrDefault(attempt => attempt.Request.Name == agent.StopWhen.FinishTool && attempt.Result.Error is null) is { } finish)
        {
            return await FinishAsync(finish.Request.Arguments.GetRawText(), ct).ConfigureAwait(false);
        }

        if (agent.HandOffOnPolicyGap
            && results.All(result => result.Error is ToolErrorCategory.NotAuthorised or ToolErrorCategory.PolicyViolation or ToolErrorCategory.ApprovalDenied))
        {
            // LOOP-11; when a human refused one of the calls, or did not answer, that is the reason (EGR-03).
            return results.Any(result => result.Error == ToolErrorCategory.ApprovalDenied)
                ? HandOff(HandoffReason.ApprovalDeniedOrTimedOut, "every tool call of the iteration was refused, and approval was denied or not given in time")
                : HandOff(HandoffReason.PolicyGap, "every tool call of the iteration was refused");
        }

        if (agent.StopWhen.ChecksPass && await OutputProblemAsync(lastText, ct).ConfigureAwait(false) is null)
        {
            return Complete(lastText);
        }

        withoutProgress = progressed ? 0 : withoutProgress + 1;
        return withoutProgress >= agent.Stall.IterationsWithoutProgress
            ? HandOff(HandoffReason.NoProgress, $"{withoutProgress} iterations in a row only repeated earlier calls and got the same results")
            : null;
    }

    /// <summary>
    /// LOOP-07: a call is progress if it is new to the turn, or if its result differs from the last time it was made. A
    /// record tool's call that the record accepts is new, since an identical proposal is not added again. The workspace
    /// (S16) adds its changes as progress.
    /// </summary>
    private bool IsProgress(ToolAttempt attempt) =>
        attempts.LastOrDefault(earlier => earlier.Request.Name == attempt.Request.Name
            && JsonElement.DeepEquals(earlier.Request.Arguments, attempt.Request.Arguments)) is not { } previous
        || previous.Result.Content != attempt.Result.Content;

    /// <summary>Gives every tool request still without a result a cancelled one, so the conversation stays valid (LOOP-09).</summary>
    private void CancelPending()
    {
        if (pending.Count == 0)
        {
            return;
        }

        var cancelled = ToolResult.Failed(ToolErrorCategory.Cancelled);
        conversation.Add(new Message(Role.User, pending.Select(call => new ToolResultContent(call.Id, cancelled.Content, isError: true))));
        attempts.AddRange(pending.Select(call => new ToolAttempt(new ToolRequest(call.Name, call.Arguments), cancelled)));
        pending = [];
    }
}
