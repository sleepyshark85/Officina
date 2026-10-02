using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>An agent's budgets (LOOP-06, RUN-05). Each limit protects INV-07: it can be raised, never removed or zero.</summary>
public sealed record AgentBudget
{
    [Setting("The limits of each turn, checked before every model call. A turn that reaches one ends in a handoff.",
        Example = """{ "iterations": 50, "cost": 5 }""")]
    [Required(ErrorMessage = "cannot be removed. Set a limit; it can be high, but it always exists.")]
    public TurnBudget Turn { get; init; } = new();

    [Setting("The limits of all the agent's turns in a run together, checked before every model call. An agent that reaches one ends its turn in a handoff; the run's budget is above it.",
        Example = """{ "cost": 10, "time": "04:00:00" }""")]
    [Required(ErrorMessage = "cannot be removed. Set a limit; it can be high, but it always exists.")]
    public TotalBudget Total { get; init; } = new();
}

/// <summary>The limits of an agent across its turns in a run (RUN-05). Each limit protects INV-07: it can be raised, never removed or zero.</summary>
public sealed record TotalBudget
{
    [Setting("The most tool calls the agent's turns may make, including tools the provider runs itself.", Example = "10000")]
    [Range(1, int.MaxValue, ErrorMessage = Messages.NotZero)]
    public int ToolCalls { get; init; } = 10_000;

    [Setting("The most tokens the agent's turns may use: input, output, cache reads and cache writes together.", Example = "100000000")]
    [Range(1, long.MaxValue, ErrorMessage = Messages.NotZero)]
    public long Tokens { get; init; } = 100_000_000;

    [Setting("The most the agent's turns may spend, in USD.", Example = "25")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public decimal Cost { get; init; } = 25m;

    [Setting("The longest the agent's turns may take, as `hh:mm:ss` or `d.hh:mm:ss`.", Example = "\"08:00:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public TimeSpan Time { get; init; } = TimeSpan.FromHours(8);
}
