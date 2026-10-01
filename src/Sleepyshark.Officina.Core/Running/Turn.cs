using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Observability;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// One turn of one agent, the primitive every pattern is built from (REQUIREMENTS.md §4.4). It calls the model, decides
/// the next step by the stop reason alone (LOOP-03, INV-01), runs the tools the model asks for through the tool
/// pipeline, and repeats until a stop condition holds (LOOP-05) or the turn must be handed off. The budget is checked
/// before every model call (LOOP-06), and the turn always ends in a result (INV-07). Each call's input is built by the
/// turn's <see cref="Conversation"/>, with the messages that arrived since the last call (CTX-08).
/// </summary>
internal sealed class Turn
{
    private readonly ToolContext context;
    private readonly ProjectOptions project;
    private readonly AgentDefinition agent;
    private readonly RunBudget runBudget;
    private readonly double cacheHitWarning;
    private readonly Conversation conversation;
    private readonly ConcurrentQueue<Message> inbox;
    private readonly IModelProvider provider;
    private readonly ModelPrice? price;
    private readonly ToolPipeline tools;
    private readonly IReadOnlyList<(string Name, IKnowledgeSource Source)> beforeTurn;
    private readonly EventBus events;
    private readonly TimeProvider time;
    private readonly string work;
    private readonly List<ToolAttempt> attempts = [];
    private readonly List<CacheWarning> cacheWarnings = [];
    private readonly List<string> retrieved = [];
    private List<ToolUseContent> pending = [];
    private long? started;
    private int iterations;
    private int toolCalls;
    private int withoutProgress;
    private Usage usage = Usage.None;
    private decimal cost;
    private string lastText = "";

    /// <param name="context">Who the turn's tool calls are made by.</param>
    /// <param name="options">The configuration the run uses.</param>
    /// <param name="provider">The provider of the agent's model profile.</param>
    /// <param name="tools">The tool pipeline built from <paramref name="options"/>.</param>
    /// <param name="knowledge">The application's knowledge sources, by extension id.</param>
    /// <param name="events">Where model text and model calls are published.</param>
    /// <param name="instructions">The agent's instructions, placeholders filled.</param>
    /// <param name="work">What the turn is asked to do.</param>
    /// <param name="inbox">Messages sent to the agent, which the turn adds to its history before each model call.</param>
    /// <param name="time">The clock for the time budgets and the operating facts.</param>
    public Turn(
        ToolContext context,
        OfficinaOptions options,
        IModelProvider provider,
        ToolPipeline tools,
        IReadOnlyDictionary<string, IKnowledgeSource> knowledge,
        EventBus events,
        string instructions,
        string work,
        ConcurrentQueue<Message> inbox,
        TimeProvider time)
    {
        this.context = context;
        project = options.Project;
        agent = options.Agents[context.Agent];
        runBudget = options.Run.Budget;
        cacheHitWarning = options.Operations.Telemetry.CacheHitWarning;
        var profile = options.Models[agent.Model];
        price = options.Providers[profile.Provider].Prices.GetValueOrDefault(profile.Model);
        conversation = new Conversation(profile, [.. tools.Offered(context.Agent)], instructions, agent.Context, provider.Capabilities);
        conversation.Add(Message.User(work));
        this.inbox = inbox;
        this.provider = provider;
        this.tools = tools;
        beforeTurn = [.. agent.Context.Retrieval.BeforeTurn.Select(name => (name, knowledge[options.Knowledge[name].ExtensionId()!]))];
        this.events = events;
        this.time = time;
        this.work = work;
    }

    public async Task<AgentResult> RunAsync(CancellationToken ct)
    {
        started = time.GetTimestamp();
        if (await RetrieveAsync(ct).ConfigureAwait(false) is { } notCovered)
        {
            return notCovered;
        }

        while (true)
        {
            if (Exhausted() is { } limit)
            {
                return HandOff(HandoffReason.BudgetExhausted, $"the {limit} budget is used up");
            }

            iterations++;
            while (inbox.TryDequeue(out var message))
            {
                conversation.Add(message);
            }

            var request = conversation.Next(Facts());
            StopReason stop;
            try
            {
                stop = await CallModelAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The exception's message may quote the request, so only its type is reported.
                return HandOff(HandoffReason.ProviderFailure, $"the model call failed: {exception.GetType().Name}");
            }

            var result = stop switch
            {
                StopReason.WantsTools => await RunToolsAsync(ct).ConfigureAwait(false),
                StopReason.Finished or StopReason.StopSequence => agent.StopWhen.Finished
                    ? Complete(lastText)
                    : HandOff(HandoffReason.NoProgress, "the model finished, but no stop condition holds"),
                StopReason.Paused => null,
                StopReason.OutputLimit => HandOff(HandoffReason.TruncatedOutput, "the reply reached the output limit"),
                StopReason.Refused => HandOff(HandoffReason.ProviderRefusal, "the model refused"),

                // S07 shortens the history once before handing off (HIST-04).
                StopReason.InputTooLong => HandOff(HandoffReason.ProviderFailure, "the input is too long for the model"),
                _ => HandOff(HandoffReason.ProviderFailure, "the model stopped for an unknown reason"),
            };
            if (result is not null)
            {
                return result;
            }

            if (iterations >= agent.StopWhen.MaxIterations)
            {
                return Complete(lastText);
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
        return new AgentResult(outcome, output, statistics, conversation.History, [.. cacheWarnings], handoff?.Invoke());
    }

    /// <summary>Time since the turn started; zero for a turn cancelled before it started.</summary>
    private TimeSpan Elapsed => started is { } at ? time.GetElapsedTime(at) : TimeSpan.Zero;

    /// <summary>
    /// Searches the knowledge sources configured before the turn with its work, once, and keeps what each found as a
    /// labelled fact of the volatile context (CTX-04). When none covers the work, the turn can end in a handoff for a
    /// policy gap (CTX-05).
    /// </summary>
    private async Task<AgentResult?> RetrieveAsync(CancellationToken ct)
    {
        var covered = false;
        foreach (var (name, source) in beforeTurn)
        {
            var retrieval = await source.RetrieveAsync(new RetrievalQuery(work, context.Caller, KnowledgeTool.MaxPassages), ct).ConfigureAwait(false);
            covered |= retrieval.Coverage != Coverage.NotCovered;
            retrieved.Add(Labels.Data($"knowledge:{name}", KnowledgeTool.Format(retrieval)));
        }

        return beforeTurn.Count > 0 && !covered && agent.Context.Retrieval.HandOffWhenNotCovered
            ? HandOff(HandoffReason.PolicyGap, "the knowledge sources do not cover the work")
            : null;
    }

    /// <summary>Which limit of the turn's or the run's budget is used up, if any (LOOP-06, COST-02).</summary>
    private string? Exhausted()
    {
        var turn = agent.Budget.Turn;
        var elapsed = Elapsed;
        return iterations >= turn.Iterations ? "turn's iteration"
            : toolCalls >= turn.ToolCalls ? "turn's tool-call"
            : usage.Total >= turn.Tokens ? "turn's token"
            : cost >= turn.Cost ? "turn's cost"
            : elapsed >= turn.Time ? "turn's time"
            : cost >= runBudget.Cost ? "run's cost"
            : elapsed >= runBudget.Time ? "run's time"
            : null;
    }

    /// <summary>The volatile context for this call: the passages retrieved before the turn, then the agent's operating facts, filled for this call (CTX-09).</summary>
    private List<string> Facts()
    {
        var now = time.GetUtcNow();
        return [.. retrieved, .. agent.Context.OperatingFacts.Select(fact => InstructionPlaceholders.Fill(fact, project, context.Agent, agent, now))];
    }

    /// <summary>
    /// Calls the model, and adds the reply to the conversation. Text arrives in pieces as it is generated, and is
    /// published as it arrives; the turn acts only once the reply is complete (MDL-07).
    /// </summary>
    private async Task<StopReason> CallModelAsync(ModelRequest request, CancellationToken ct)
    {
        using var activity = Telemetry.StartModelCall(context, request.Profile.Model);
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
                    await tools.AuditProviderToolAsync(context, used.Request, used.Result, ct).ConfigureAwait(false);
                    break;
                case UsageReported reported:
                    var spent = price?.Cost(reported.Usage) ?? 0m;
                    (callUsage, callCost) = (callUsage + reported.Usage, callCost + spent);
                    (usage, cost) = (usage + reported.Usage, cost + spent);
                    break;
                case Stopped stopped:
                    stop = stopped.Reason;
                    break;
            }
        }

        Telemetry.ModelCallEnded(activity, context, request.Profile.Model, stop, callUsage, callCost, time.GetElapsedTime(callStarted));
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
        conversation.Add(new Message(
            Role.User, pending.Zip(results, (call, result) => new ToolResultContent(call.Id, Labels.Data($"tool:{call.Name}", result.Content), result.Error is not null))));
        pending = [];
        toolCalls += requests.Count;
        var batch = requests.Zip(results, (call, result) => new ToolAttempt(call, result)).ToList();
        var progressed = batch.Any(IsProgress);
        attempts.AddRange(batch);

        if (batch.FirstOrDefault(attempt => attempt.Result.RouteTo is not null) is { } routed)
        {
            return HandOff(HandoffReason.RoutedByGate, routed.Result.Content, routed.Result.RouteTo, routed.Request);
        }

        if (batch.FirstOrDefault(attempt => attempt.Request.Name == agent.StopWhen.FinishTool && attempt.Result.Error is null) is { } finish)
        {
            return Complete(finish.Request.Arguments.GetRawText());
        }

        if (agent.HandOffOnPolicyGap && results.All(result => result.Error is ToolErrorCategory.NotAuthorised or ToolErrorCategory.PolicyViolation))
        {
            return HandOff(HandoffReason.PolicyGap, "every tool call of the iteration was refused"); // LOOP-11
        }

        withoutProgress = progressed ? 0 : withoutProgress + 1;
        return withoutProgress >= agent.Stall.IterationsWithoutProgress
            ? HandOff(HandoffReason.NoProgress, $"{withoutProgress} iterations in a row only repeated earlier calls and got the same results")
            : null;
    }

    /// <summary>
    /// LOOP-07: a call is progress if it is new to the turn, or if its result differs from the last time it was made.
    /// The run record (S06) and the workspace (S14) add their own changes as progress.
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
