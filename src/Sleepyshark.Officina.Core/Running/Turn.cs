using System.Text;
using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// One turn of one agent, the primitive every pattern is built from (REQUIREMENTS.md §4.4). It calls the model, decides
/// the next step by the stop reason alone (LOOP-03, INV-01), runs the tools the model asks for through the tool
/// pipeline, and repeats until a stop condition holds (LOOP-05) or the turn must be handed off. The budget is checked
/// before every model call (LOOP-06), and the turn always ends in a result (INV-07).
/// </summary>
internal sealed class Turn
{
    private readonly ToolContext context;
    private readonly AgentDefinition agent;
    private readonly RunBudget runBudget;
    private readonly ModelRequest request;
    private readonly IModelProvider provider;
    private readonly ModelPrice? price;
    private readonly ToolPipeline tools;
    private readonly TimeProvider time;
    private readonly string work;
    private readonly List<Message> transcript;
    private readonly List<ToolAttempt> attempts = [];
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
    /// <param name="instructions">The agent's instructions, placeholders filled.</param>
    /// <param name="work">What the turn is asked to do.</param>
    /// <param name="time">The clock for the time budgets.</param>
    public Turn(ToolContext context, OfficinaOptions options, IModelProvider provider, ToolPipeline tools, string instructions, string work, TimeProvider time)
    {
        this.context = context;
        agent = options.Agents[context.Agent];
        runBudget = options.Run.Budget;
        var profile = options.Models[agent.Model];
        price = options.Providers[profile.Provider].Prices.GetValueOrDefault(profile.Model);
        request = new ModelRequest(profile, instructions, [], [.. tools.Offered(context.Agent)]);
        this.provider = provider;
        this.tools = tools;
        this.time = time;
        this.work = work;
        transcript = [Message.User(work)];
    }

    public async Task<AgentResult> RunAsync(CancellationToken ct)
    {
        started = time.GetTimestamp();
        while (true)
        {
            if (Exhausted() is { } limit)
            {
                return HandOff(HandoffReason.BudgetExhausted, $"the {limit} budget is used up");
            }

            iterations++;
            StopReason stop;
            try
            {
                stop = await CallModelAsync(ct).ConfigureAwait(false);
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
        return new AgentResult(outcome, output, statistics, [.. transcript], handoff?.Invoke());
    }

    /// <summary>Time since the turn started; zero for a turn cancelled before it started.</summary>
    private TimeSpan Elapsed => started is { } at ? time.GetElapsedTime(at) : TimeSpan.Zero;

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

    /// <summary>
    /// Calls the model with input rebuilt from the transcript (LOOP-10), and adds the reply to it. Text arrives in
    /// pieces as it is generated; the turn acts only once the reply is complete (MDL-07).
    /// </summary>
    private async Task<StopReason> CallModelAsync(CancellationToken ct)
    {
        var stop = StopReason.Unknown;
        var reply = new List<Content>();
        var text = new StringBuilder();
        await foreach (var modelEvent in provider.StreamAsync(request with { Messages = [.. transcript] }, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            switch (modelEvent)
            {
                case TextDelta delta:
                    text.Append(delta.Text);
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
                    usage += reported.Usage;
                    cost += price?.Cost(reported.Usage) ?? 0m;
                    break;
                case Stopped stopped:
                    stop = stopped.Reason;
                    break;
            }
        }

        AddText();
        if (reply.Count > 0)
        {
            transcript.Add(new Message(Role.Assistant, reply));
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

    /// <summary>Runs the calls of the last reply, and decides whether the turn ends with them; null when it goes on.</summary>
    private async Task<AgentResult?> RunToolsAsync(CancellationToken ct)
    {
        if (pending.Count == 0)
        {
            return HandOff(HandoffReason.ProviderFailure, "the model asked for tools but requested none"); // LOOP-04
        }

        var requests = pending.Select(call => new ToolRequest(call.Name, call.Arguments)).ToList();
        var results = await tools.RunAsync(context, requests, ct).ConfigureAwait(false);
        transcript.Add(new Message(Role.User, pending.Zip(results, (call, result) => new ToolResultContent(call.Id, result.Content, result.Error is not null))));
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

    /// <summary>Gives every tool request still without a result a cancelled one, so the transcript stays valid (LOOP-09).</summary>
    private void CancelPending()
    {
        if (pending.Count == 0)
        {
            return;
        }

        var cancelled = ToolResult.Failed(ToolErrorCategory.Cancelled);
        transcript.Add(new Message(Role.User, pending.Select(call => new ToolResultContent(call.Id, cancelled.Content, isError: true))));
        attempts.AddRange(pending.Select(call => new ToolAttempt(new ToolRequest(call.Name, call.Arguments), cancelled)));
        pending = [];
    }
}
