using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// What one level of a run may spend, and has spent: the run, an agent (RUN-05), a pattern, or a turn (LOOP-06). What a
/// level spends counts at every level above it, so a step draws on its pattern's budget, the agent's and the run's (PAT-06).
/// Its time is the time it has run, less the time it waited for the owner: while a turn under it waits for an answer or is
/// paused, no level it draws on uses time, so how quickly the owner answers never uses up a budget.
/// </summary>
internal sealed class Budget
{
    private readonly Lock gate = new();
    private readonly string level;
    private readonly TurnBudget limits;
    private readonly Budget? parent;
    private readonly Budget? alongside;
    private readonly TimeProvider time;
    private readonly long started;
    private readonly TimeSpan timeBefore;
    private readonly HashSet<string> warned;
    private int iterations;
    private int toolCalls;
    private long tokens;
    private decimal cost;
    private int allowances = 1;
    private int waits;
    private long waitStarted;
    private TimeSpan waited;

    private Budget(string level, TurnBudget limits, Budget? parent, TimeProvider time, Spent before, Budget? alongside = null)
    {
        this.alongside = alongside;
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

    /// <summary>The run's budget: its cost and time, and its tokens and tool calls when set. A run that resumes has spent some already (INV-07).</summary>
    public static Budget ForRun(RunBudget run, TimeProvider time, Spent before = default) =>
        new("run's", new TurnBudget { Iterations = int.MaxValue, ToolCalls = run.ToolCalls ?? int.MaxValue, Tokens = run.Tokens ?? long.MaxValue, Cost = run.Cost, Time = run.Time }, null, time, before);

    /// <summary>A budget drawn from this one, starting now.</summary>
    /// <param name="level">Its name in a handoff's detail, such as <c>turn's</c>.</param>
    /// <param name="limits">Its own limits.</param>
    /// <param name="before">What the level had spent before, which the levels above already count.</param>
    public Budget Draw(string level, TurnBudget limits, Spent before = default) => new(level, limits, this, time, before);

    /// <summary>
    /// This budget, with another level beside it that is not above it, such as the agent level of a pattern's step agent: what is
    /// spent counts at both, and either can be used up (RUN-05).
    /// </summary>
    public Budget Alongside(Budget other) =>
        new("", new TurnBudget { Iterations = int.MaxValue, ToolCalls = int.MaxValue, Tokens = long.MaxValue, Cost = decimal.MaxValue, Time = TimeSpan.MaxValue }, this, time, default, other);

    /// <summary>An agent's own level over all its turns in a run, beside the levels its turns draw on (RUN-05).</summary>
    public static Budget ForAgent(TotalBudget total, TimeProvider time, Spent before) =>
        new("agent's", Limits(total), null, time, before);

    /// <summary>An agent's budget over all its turns in the run (RUN-05).</summary>
    public Budget DrawAgent(TotalBudget? total, Spent before) =>
        total is null ? this : Draw("agent's", Limits(total), before);

    private static TurnBudget Limits(TotalBudget total) => new()
    {
        Iterations = int.MaxValue, ToolCalls = total.ToolCalls ?? int.MaxValue, Tokens = total.Tokens ?? long.MaxValue, Cost = total.Cost ?? decimal.MaxValue, Time = TimeSpan.MaxValue,
    };

    /// <summary>
    /// Stops this level's clock, and every level's it draws on, until the result is disposed: a turn under it waits for the owner,
    /// to answer or to resume it. Waits that overlap stop the clock once.
    /// </summary>
    public IDisposable WaitForOwner()
    {
        Waiting(1);
        return new Waited(this);
    }

    private void Waiting(int change)
    {
        lock (gate)
        {
            if (change > 0 && waits++ == 0)
            {
                waitStarted = time.GetTimestamp();
            }
            else if (change < 0 && --waits == 0)
            {
                waited += time.GetElapsedTime(waitStarted);
            }
        }

        parent?.Waiting(change);
        alongside?.Waiting(change);
    }

    /// <summary>The time the level has used: since it started, with what it used before, less its waits for the owner.</summary>
    public TimeSpan Time
    {
        get
        {
            lock (gate)
            {
                return Used();
            }
        }
    }

    /// <summary><see cref="Time"/>, read under the lock.</summary>
    private TimeSpan Used() => timeBefore + time.GetElapsedTime(started) - waited - (waits > 0 ? time.GetElapsedTime(waitStarted) : TimeSpan.Zero);

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
        alongside?.Spend(iterations, toolCalls, tokens, cost);
    }

    /// <summary>
    /// The owner lets the run go on past its budget: it may spend as much again (RUN-05, HITL-04). Only once for the extensions
    /// seen when the owner was asked, so several agents asked at once extend it once.
    /// </summary>
    /// <param name="seen">The run's extensions when it was found used up, from <see cref="Exhausted"/>.</param>
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

    /// <summary>
    /// The limit used up, here or above, such as <c>turn's iteration</c>, whether it is the run's, and how many times the owner had
    /// let the run go on when it was checked; null when none is.
    /// </summary>
    public (string Limit, bool OfRun, int Extensions)? Exhausted()
    {
        string? limit;
        int extensions;
        lock (gate)
        {
            // Allowances multiply the limits of the run only: every other level has one.
            limit = iterations >= limits.Iterations ? "iteration"
                : toolCalls >= (decimal)limits.ToolCalls * allowances ? "tool-call"
                : tokens >= (decimal)limits.Tokens * allowances ? "token"
                : cost >= limits.Cost * allowances ? "cost"
                : Used() >= limits.Time * allowances ? "time"
                : null;
            extensions = allowances - 1;
        }

        return limit is not null ? ($"{level} {limit}", parent is null, extensions) : parent?.Exhausted() ?? AlongsideExhausted();

        // A level beside the chain is never the run's, so the owner is not asked to extend it.
        (string, bool, int)? AlongsideExhausted() => alongside?.Exhausted() is { } found ? (found.Limit, false, 0) : null;
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
                ("iteration", (double)iterations / limits.Iterations), ("tool-call", toolCalls / ((double)limits.ToolCalls * allowances)), ("token", tokens / ((double)limits.Tokens * allowances)),
                ("cost", (double)(cost / (limits.Cost * allowances))), ("time", Used() / (limits.Time * allowances)),
            })
            {
                if (used >= WarnAt && used < 1 && warned.Add(limit))
                {
                    found.Add((level, limit, used));
                }
            }
        }

        return [.. found, .. parent?.Warnings() ?? [], .. alongside?.Warnings() ?? []];
    }

    /// <summary>The share of a limit at which a budget warns.</summary>
    public const double WarnAt = 0.8;

    /// <summary>A wait for the owner, which ends once.</summary>
    private sealed class Waited(Budget budget) : IDisposable
    {
        private int ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) == 0)
            {
                budget.Waiting(-1);
            }
        }
    }
}

/// <summary>What a level had spent before this process took the run up again (INV-07).</summary>
internal readonly record struct Spent(decimal Cost, long Tokens, int ToolCalls, TimeSpan Time, IReadOnlyCollection<string>? Warned = null)
{
    /// <summary>
    /// What a run and its agent spent, from the run's stored events: the cost and tokens of its model calls, its tool calls,
    /// the budget warnings given, and the time its turns ran, less the time they waited for the owner to answer, as the budget
    /// counts it. Time outside turns is not counted, and neither is the time between a process dying and the run being rolled
    /// back or resumed, so a turn that never ended counts up to its last event only. The owner's pauses are not stored, so those
    /// before a restart count. A run is one agent's work, its pattern's steps and its team's agents included, so the agent has
    /// spent all the run has.
    /// </summary>
    public static (Spent Run, Spent Agent) Of(IReadOnlyList<CoreEvent> events)
    {
        var time = TimeSpan.Zero;
        var (turns, asked) = (0, 0);
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Payload is RunResumed or RunRolledBack)
            {
                (turns, asked) = (0, 0); // a new process: whatever was open when the last one died is over
                continue;
            }

            if (i > 0 && turns > 0 && asked == 0)
            {
                time += events[i].Time - events[i - 1].Time;
            }

            turns += events[i].Payload switch { TurnStarted => 1, TurnEnded when turns > 0 => -1, _ => 0 };
            asked += events[i].Payload switch { HumanAsked => 1, HumanAnswered when asked > 0 => -1, _ => 0 };
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
