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
    private readonly HashSet<string> warned;
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
        warned = [.. before.Warned ?? []];
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
    public Budget DrawAgent(TotalBudget? total, Spent before) =>
        total is null ? this : Draw(
            "agent's",
            new TurnBudget
            {
                Iterations = int.MaxValue, ToolCalls = total.ToolCalls ?? int.MaxValue, Tokens = total.Tokens ?? long.MaxValue, Cost = total.Cost ?? decimal.MaxValue, Time = TimeSpan.MaxValue,
            },
            before);

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

    /// <summary>How many times the owner has let the run go on past its budget.</summary>
    public int RunExtensions
    {
        get
        {
            if (parent is not null)
            {
                return parent.RunExtensions;
            }

            lock (gate)
            {
                return allowances - 1;
            }
        }
    }

    /// <summary>
    /// The owner lets the run go on past its budget: it may spend as much again (RUN-05, HITL-04). Only once for the extensions
    /// seen when the owner was asked, so several agents asked at once extend it once.
    /// </summary>
    /// <param name="seen">The run's <see cref="RunExtensions"/> when the owner was asked.</param>
    public void ExtendRun(int seen)
    {
        if (parent is not null)
        {
            parent.ExtendRun(seen);
            return;
        }

        lock (gate)
        {
            if (allowances - 1 == seen)
            {
                allowances++;
                warned.Clear();
            }
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
internal readonly record struct Spent(decimal Cost, long Tokens, int ToolCalls, TimeSpan Time, IReadOnlyCollection<string>? Warned = null)
{
    /// <summary>
    /// What a run and its agent spent, from the run's stored events: the cost and tokens of its model calls, its tool calls,
    /// the budget warnings given, and the time its turns ran. Time outside turns is not counted, and neither is the time between a
    /// process dying and the run being rolled back or resumed, so a turn that never ended counts up to its last event only. A
    /// run is one agent's work, its pattern's steps and its team's agents included, so the agent has spent all the run has.
    /// </summary>
    public static (Spent Run, Spent Agent) Of(IReadOnlyList<CoreEvent> events)
    {
        var time = TimeSpan.Zero;
        var turns = 0;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Payload is RunResumed or RunRolledBack)
            {
                turns = 0; // a new process: whatever was open when the last one died is over
                continue;
            }

            if (i > 0 && turns > 0)
            {
                time += events[i].Time - events[i - 1].Time;
            }

            turns += events[i].Payload switch { TurnStarted => 1, TurnEnded when turns > 0 => -1, _ => 0 };
        }

        var run = Of(events, "run's") with { Time = time };
        return (run, run with { Warned = Of(events, "agent's").Warned });
    }

    /// <summary>
    /// What one agent of a team spent, by its id, from the run's stored events, for its own budget over its turns (RUN-05). Only
    /// its own turns draw on that budget, so its warnings are its own.
    /// </summary>
    public static Spent OfAgent(IReadOnlyList<CoreEvent> events, string agentId) => Of(events.Where(coreEvent => coreEvent.Agent == agentId), "agent's");

    private static Spent Of(IEnumerable<CoreEvent> events, string level) => events.Aggregate(default(Spent), (spent, coreEvent) => coreEvent.Payload switch
    {
        ModelCallEnded call => spent with { Cost = spent.Cost + call.Cost, Tokens = spent.Tokens + call.Usage.Total },
        ToolCallEnded => spent with { ToolCalls = spent.ToolCalls + 1 },
        BudgetWarning warning when warning.Level == level => spent with { Warned = [.. spent.Warned ?? [], warning.Limit] },
        _ => spent,
    });
}
