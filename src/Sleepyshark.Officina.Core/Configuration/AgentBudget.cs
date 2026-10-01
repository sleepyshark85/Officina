using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>An agent's budgets (LOOP-06). Each limit protects INV-07: it can be raised, never removed or zero.</summary>
public sealed record AgentBudget
{
    [Setting("The limits of each turn, checked before every model call. A turn that reaches one ends in a handoff.",
        Example = """{ "iterations": 50, "cost": 5 }""")]
    [Required(ErrorMessage = "cannot be removed. Set a limit; it can be high, but it always exists.")]
    public TurnBudget Turn { get; init; } = new();
}
