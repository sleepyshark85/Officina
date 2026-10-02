using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Observability;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// The steps of one run. The agent does its work in a turn, or in a pattern whose steps are turns or nested patterns
/// (PAT-01, PAT-02), all built from the one turn primitive. Each step draws on its pattern's budget, and reports its
/// outcome in an event, a span and a log entry (PAT-06, PAT-08). The run's result gathers what all its turns used and
/// produced. Content flows between steps, so once a step has read untrusted content, every step of the run is marked too.
/// </summary>
/// <param name="options">The configuration the run uses.</param>
/// <param name="patterns">The application's patterns, by extension id.</param>
/// <param name="checks">The application's checks, by extension id.</param>
/// <param name="events">Where step outcomes are published.</param>
/// <param name="newTurn">Makes a turn of the context's agent on its work, with its budget drawn from the one given.</param>
/// <param name="run">The run, and the agent it is for.</param>
/// <param name="work">The work, as admission passed it.</param>
/// <param name="time">The clock for the run's elapsed time.</param>
/// <param name="checkpoint">Takes a checkpoint of the run if its configuration asks for one at the point (RUN-03).</param>
/// <param name="team">What a team needs of the runner (TEAM).</param>
internal sealed class Steps(
    OfficinaOptions options,
    IReadOnlyDictionary<string, ILoopPattern> patterns,
    IReadOnlyDictionary<string, ICheck> checks,
    EventBus events,
    Func<ToolContext, Work, Budget, Turn> newTurn,
    ToolContext run,
    Work work,
    TimeProvider time,
    Func<ToolContext, CheckpointPoint, CancellationToken, Task<Checkpoint?>> checkpoint,
    TeamServices team)
{
    private static readonly Dictionary<string, ILoopPattern> BuiltIn = new(StringComparer.Ordinal)
    {
        [PatternOptions.Workflow] = new Workflow(),
        [PatternOptions.Router] = new Router(),
        [PatternOptions.FanOut] = new FanOut(),
        [PatternOptions.EvaluateAndRevise] = new EvaluateAndRevise(),
        [PatternOptions.PlanAndExecute] = new PlanAndExecute(),
    };

    private readonly ConcurrentQueue<AgentResult> turns = new();
    private long? started;

    /// <summary>The run's work, as admission passed it.</summary>
    public string RunInput => work.Input;

    /// <summary>
    /// Does the agent's work: its turn, or its pattern, whose budget is the agent's turn budget drawn from the agent's. A team's
    /// agents draw on the run's budget, each with its own: the team is the run's work.
    /// </summary>
    public Task<StepResult> RunAsync(Budget budget, CancellationToken ct)
    {
        started = time.GetTimestamp();
        var agent = options.Agents[run.Agent];
        return agent.Pattern.IsTurn ? RunTurnAsync(run, RunInput, budget, ct)
            : agent.Pattern.Type == PatternOptions.Team ? new TeamRun(this, team, options, run, agent.Pattern, RunInput, budget).RunAsync(ct)
            : RunPatternAsync(run, agent.Pattern, RunInput, budget.Draw("pattern's", agent.Budget.Turn), ct);
    }

    /// <summary>Takes a checkpoint of the run if its configuration asks for one at the point (RUN-03).</summary>
    public Task<Checkpoint?> CheckpointAsync(ToolContext context, CheckpointPoint point, CancellationToken ct) => checkpoint(context, point, ct);

    /// <summary>Publishes an event of the run.</summary>
    public ValueTask PublishAsync(ToolContext context, EventPayload payload, CancellationToken ct) => events.PublishAsync(context, payload, ct);

    /// <summary>Runs a step of a pattern; it always ends in a result (REL-02).</summary>
    /// <param name="context">The pattern's agent, and the step's path.</param>
    /// <param name="step">The step.</param>
    /// <param name="input">All the step gets.</param>
    /// <param name="budget">The pattern's budget.</param>
    /// <param name="ct">Cancels the step.</param>
    public async Task<StepResult> RunStepAsync(ToolContext context, StepOptions step, string input, Budget budget, CancellationToken ct)
    {
        var agent = step.Agent is { } named ? options.Agents[named] : null;
        context = context with { Agent = step.Agent ?? context.Agent };
        using var activity = Telemetry.StartStep(context);
        StepResult result;
        try
        {
            result = step.Pattern is { IsTurn: false } nested ? await RunPatternAsync(context, nested, input, budget, ct).ConfigureAwait(false)
                : agent is { Pattern.IsTurn: false } ? await RunPatternAsync(context, agent.Pattern, input, budget.Draw("pattern's", agent.Budget.Turn), ct).ConfigureAwait(false)
                : await RunTurnAsync(context, input, budget, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
            {
                await checkpoint(context, CheckpointPoint.Step, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = Cancelled("the step was cancelled");
        }
        catch (Exception exception)
        {
            result = new(StepOutcome.Failed, $"the step failed: {exception.GetType().Name}");
        }

        await events.PublishAsync(context, new StepEnded(result.Outcome, result.Handoff?.Reason), CancellationToken.None).ConfigureAwait(false);
        Telemetry.StepEnded(activity, result.Outcome.ToString());
        OfficinaLog.Log.StepEnded(context.RunId, context.Agent, context.Step!, result.Outcome.ToString());
        return result;
    }

    /// <summary>The first of the named checks the output fails, with its findings masked; null when it passes them all.</summary>
    public async Task<string?> CheckAsync(ToolContext context, IReadOnlyList<string> names, string output, CancellationToken ct)
    {
        var failed = await Turn.FailedCheckAsync(
            context, events, names.Select(name => (name, checks[options.Checks[name].Id(name)])), output, [.. turns.SelectMany(turn => turn.Artifacts)], ct).ConfigureAwait(false);

        // A check sees the artifacts as tools produced them, so its findings are masked before a model sees them (ING-02).
        return failed is null ? null : context.Masker?.Mask(failed) ?? failed;
    }

    /// <summary>A step or run that was cancelled, handed off to the step that started the work.</summary>
    public StepResult Cancelled(string detail) =>
        new(StepOutcome.Cancelled, detail, new Handoff(HandoffReason.RequestedByHuman, null, detail, RunInput, [], null, ""));

    /// <summary>
    /// The run's result (EGR-01): how it ended, and what all its turns used and produced. The transcript holds each
    /// turn's conversation in the order the turns ended, and the record is as the last turn read it.
    /// </summary>
    public AgentResult Result(StepResult ended)
    {
        var all = turns.ToArray();
        var statistics = new TurnStatistics(
            all.Sum(turn => turn.Statistics.Iterations),
            all.Sum(turn => turn.Statistics.ToolCalls),
            all.Aggregate(Usage.None, (usage, turn) => usage + turn.Statistics.Usage),
            all.Sum(turn => turn.Statistics.Cost),
            started is { } at ? time.GetElapsedTime(at) : TimeSpan.Zero);
        var outcome = ended.Outcome switch
        {
            StepOutcome.Completed => AgentOutcome.Completed,
            StepOutcome.Failed => AgentOutcome.Failed,
            _ => AgentOutcome.HandedOff,
        };
        return new AgentResult(outcome, ended.Output, statistics, [.. all.SelectMany(turn => turn.Transcript)], [.. all.SelectMany(turn => turn.CacheWarnings)],
            all.LastOrDefault()?.Record ?? [], [.. all.SelectMany(turn => turn.Artifacts)], ended.Handoff);
    }

    private async Task<StepResult> RunPatternAsync(ToolContext context, PatternOptions pattern, string input, Budget budget, CancellationToken ct)
    {
        var implementation = BuiltIn.GetValueOrDefault(pattern.Type) ?? patterns[pattern.ExtensionId()!];
        return await implementation.RunAsync(new PatternContext(this, context, pattern, input, budget), ct).ConfigureAwait(false);
    }

    private async Task<StepResult> RunTurnAsync(ToolContext context, string input, Budget budget, CancellationToken ct)
    {
        var turn = newTurn(context, work with { Input = input }, budget);
        AgentResult result;
        try
        {
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

        turns.Enqueue(result);
        if (!ct.IsCancellationRequested)
        {
            await checkpoint(context, CheckpointPoint.Turn, ct).ConfigureAwait(false);
        }

        var outcome = result.Outcome switch
        {
            AgentOutcome.Completed => StepOutcome.Completed,
            AgentOutcome.HandedOff when ct.IsCancellationRequested => StepOutcome.Cancelled,
            AgentOutcome.HandedOff => StepOutcome.HandedOff,
            _ => StepOutcome.Failed,
        };
        return new(outcome, result.Output, result.Handoff);
    }
}
