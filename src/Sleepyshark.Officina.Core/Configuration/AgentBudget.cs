using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>An agent's budgets (LOOP-06, RUN-05). Each limit protects INV-07: it can be raised, never removed or zero.</summary>
public sealed record AgentBudget
{
    [Setting("The limits of each turn, checked before every model call. A turn that reaches one ends in a handoff.",
        Example = """{ "iterations": 50, "cost": 5 }""")]
    [Required(ErrorMessage = "cannot be removed. Set a limit; it can be high, but it always exists.")]
    public TurnBudget Turn { get; init; } = new();

    [Setting("The limits of all the agent's turns in a run together, checked before every model call. Unset means the agent has no limit of its own beyond its turns' and the run's. An agent that reaches one ends its turn in a handoff.",
        Example = """{ "cost": 10, "toolCalls": 500 }""")]
    public TotalBudget? Total { get; init; }
}

/// <summary>The limits of an agent across its turns in a run (RUN-05). A limit that is unset does not apply.</summary>
public sealed record TotalBudget
{
    [Setting("The most tool calls the agent's turns may make, including tools the provider runs itself.", Example = "10000")]
    [Range(1, int.MaxValue, ErrorMessage = Messages.NotZero)]
    public int? ToolCalls { get; init; }

    [Setting("The most tokens the agent's turns may use: input, output, cache reads and cache writes together.", Example = "100000000")]
    [Range(1, long.MaxValue, ErrorMessage = Messages.NotZero)]
    public long? Tokens { get; init; }

    [Setting("The most the agent's turns may spend, in USD.", Example = "25")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public decimal? Cost { get; init; }
}
