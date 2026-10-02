using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// What one level of a run may spend, and has spent: the run (RUN-05), a pattern, or a turn (LOOP-06). What a level
/// spends counts at every level above it, so a step draws on its pattern's budget and the run's (PAT-06).
/// </summary>
internal sealed class Budget
{
    private readonly Lock gate = new();
    private readonly string level;
    private readonly TurnBudget limits;
    private readonly Budget? parent;
    private readonly TimeProvider time;
    private readonly long started;
    private int iterations;
    private int toolCalls;
    private long tokens;
    private decimal cost;
    private int allowances = 1;

    private Budget(string level, TurnBudget limits, Budget? parent, TimeProvider time)
    {
        this.level = level;
        this.limits = limits;
        this.parent = parent;
        this.time = time;
        started = time.GetTimestamp();
    }

    /// <summary>The run's budget, which limits only its cost and time.</summary>
    public static Budget ForRun(RunBudget run, TimeProvider time) =>
        new("run's", new TurnBudget { Iterations = int.MaxValue, ToolCalls = int.MaxValue, Tokens = long.MaxValue, Cost = run.Cost, Time = run.Time }, null, time);

    /// <summary>A budget drawn from this one, starting now.</summary>
    /// <param name="level">Its name in a handoff's detail, such as <c>turn's</c>.</param>
    /// <param name="limits">Its own limits.</param>
    public Budget Draw(string level, TurnBudget limits) => new(level, limits, this, time);

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
                : time.GetElapsedTime(started) >= limits.Time * allowances ? "time"
                : null;
        }

        return limit is null ? parent?.Exhausted() : ($"{level} {limit}", parent is null);
    }
}
