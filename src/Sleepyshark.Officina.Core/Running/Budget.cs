using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// What one level of a run may spend, and has spent: the run, an agent (RUN-05), a pattern, or a turn (LOOP-06). What a
/// level spends counts at every level above it, so a step draws on its pattern's budget, the agent's and the run's (PAT-06).
/// </summary>
internal sealed class Budget
{
    private readonly Lock gate = new();
    private readonly string level;
    private readonly TurnBudget limits;
    private readonly Budget? parent;
    private readonly TimeProvider time;
    private readonly long started;
    private readonly TimeSpan timeBefore;
    private readonly HashSet<string> warned = [];
    private int iterations;
    private int toolCalls;
    private long tokens;
    private decimal cost;
    private int allowances = 1;

    private Budget(string level, TurnBudget limits, Budget? parent, TimeProvider time, Spent before)
    {
        cost = before.Cost;
        tokens = before.Tokens;
        toolCalls = before.ToolCalls;
        timeBefore = before.Time;
        this.level = level;
        this.limits = limits;
        this.parent = parent;
        this.time = time;
        started = time.GetTimestamp();
    }

    /// <summary>The run's budget, which limits only its cost and time. A run that resumes has spent some already (INV-07).</summary>
    public static Budget ForRun(RunBudget run, TimeProvider time, Spent before = default) =>
        new("run's", new TurnBudget { Iterations = int.MaxValue, ToolCalls = int.MaxValue, Tokens = long.MaxValue, Cost = run.Cost, Time = run.Time }, null, time, before);

    /// <summary>A budget drawn from this one, starting now.</summary>
    /// <param name="level">Its name in a handoff's detail, such as <c>turn's</c>.</param>
    /// <param name="limits">Its own limits.</param>
    /// <param name="before">What the level had spent before, which the levels above already count.</param>
    public Budget Draw(string level, TurnBudget limits, Spent before = default) => new(level, limits, this, time, before);

    /// <summary>An agent's budget over all its turns in the run (RUN-05).</summary>
    public Budget DrawAgent(TotalBudget total, Spent before) =>
        Draw("agent's", new TurnBudget { Iterations = int.MaxValue, ToolCalls = total.ToolCalls, Tokens = total.Tokens, Cost = total.Cost, Time = total.Time }, before);

    public void Spend(int iterations = 0, int toolCalls = 0, long tokens = 0, decimal cost = 0m)
    {
        lock (gate)
        {
            this.iterations += iterations;
            this.toolCalls += toolCalls;
            this.tokens += tokens;
            this.cost += cost;
        }

        parent?.Spend(iterations, toolCalls, tokens, cost);
    }

    /// <summary>The owner lets the run go on past its budget: it may spend as much again (RUN-05, HITL-04).</summary>
    public void ExtendRun()
    {
        if (parent is null)
        {
            lock (gate)
            {
                allowances++;
                warned.Clear();
            }
        }
        else
        {
            parent.ExtendRun();
        }
    }

    /// <summary>The limit used up, here or above, such as <c>turn's iteration</c>, and whether it is the run's; null when none is.</summary>
    public (string Limit, bool OfRun)? Exhausted()
    {
        string? limit;
        lock (gate)
        {
            limit = iterations >= limits.Iterations ? "iteration"
                : toolCalls >= limits.ToolCalls ? "tool-call"
                : tokens >= limits.Tokens ? "token"
                : cost >= limits.Cost * allowances ? "cost"
                : timeBefore + time.GetElapsedTime(started) >= limits.Time * allowances ? "time"
                : null;
        }

        return limit is null ? parent?.Exhausted() : ($"{level} {limit}", parent is null);
    }

    /// <summary>
    /// The limits, here or above, that have newly used <see cref="WarnAt"/> of what they allow, each reported once (EVT-01).
    /// A limit that is used up is reported by the handoff instead.
    /// </summary>
    public IReadOnlyList<(string Level, string Limit, double Used)> Warnings()
    {
        var found = new List<(string, string, double)>();
        lock (gate)
        {
            foreach (var (limit, used) in new[]
            {
                ("iteration", (double)iterations / limits.Iterations), ("tool-call", (double)toolCalls / limits.ToolCalls), ("token", (double)tokens / limits.Tokens),
                ("cost", (double)(cost / (limits.Cost * allowances))), ("time", (timeBefore + time.GetElapsedTime(started)) / (limits.Time * allowances)),
            })
            {
                if (used >= WarnAt && used < 1 && warned.Add(limit))
                {
                    found.Add((level, limit, used));
                }
            }
        }

        return parent is null ? found : [.. found, .. parent.Warnings()];
    }

    /// <summary>The share of a limit at which a budget warns.</summary>
    public const double WarnAt = 0.8;
}

/// <summary>What a level had spent before this process took the run up again (INV-07).</summary>
internal readonly record struct Spent(decimal Cost, long Tokens, int ToolCalls, TimeSpan Time)
{
    /// <summary>
    /// What a run and one of its agents spent, from the run's stored events: the cost and tokens of its model calls, its tool calls,
    /// and the time it was running, which leaves out the time between a process dying and the run resuming.
    /// </summary>
    public static (Spent Run, Spent Agent) Of(IReadOnlyList<CoreEvent> events, string agent)
    {
        var time = TimeSpan.Zero;
        for (var i = 1; i < events.Count; i++)
        {
            time += events[i].Payload is RunResumed ? TimeSpan.Zero : events[i].Time - events[i - 1].Time;
        }

        return (Of(events) with { Time = time }, Of(events.Where(coreEvent => coreEvent.Agent == agent)) with { Time = time });

        static Spent Of(IEnumerable<CoreEvent> events) => events.Aggregate(default(Spent), (spent, coreEvent) => coreEvent.Payload switch
        {
            ModelCallEnded call => spent with { Cost = spent.Cost + call.Cost, Tokens = spent.Tokens + call.Usage.Total },
            ToolCallEnded => spent with { ToolCalls = spent.ToolCalls + 1 },
            _ => spent,
        });
    }
}
