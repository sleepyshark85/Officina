namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Defaults for every run.</summary>
public sealed record RunDefaults
{
    // INV-07: a run always has a budget.
    [Setting("The run's budget. It can be high, but it cannot be removed or unlimited.",
        Example = """{ "cost": 25, "time": "8h" }""", Invariant = "INV-07", Live = true)]
    public RunBudget Budget { get; init; } = new();

    [Setting("How tool calls that need permission are decided: `ask` the owner, `auto` by the rules, or `readOnly`.",
        Example = "\"ask\"", Live = true)]
    public PermissionMode PermissionMode { get; init; } = PermissionMode.Ask;
}
