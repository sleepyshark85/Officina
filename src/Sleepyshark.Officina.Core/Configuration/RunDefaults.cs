namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Defaults for every run.</summary>
public sealed record RunDefaults
{
    [Setting("The run's budget. It can be high, but never removed or unlimited (RUN-05).", Example = """{ "cost": 25, "time": "8h" }""", Invariant = "INV-07", Live = true)]
    public RunBudget Budget { get; init; } = new();

    [Setting("How tool calls that need permission are decided: `ask` the owner, `auto` by the rules, or `readOnly` (HITL-01).", Example = "\"ask\"", Live = true)]
    public PermissionMode PermissionMode { get; init; } = PermissionMode.Ask;
}

/// <summary>The limits of one run.</summary>
public sealed record RunBudget
{
    [Setting("The most the run may spend, in USD.", Example = "25", Minimum = 0, ExclusiveMinimum = true, Invariant = "INV-07", Live = true)]
    public decimal Cost { get; init; } = 25m;

    [Setting("The longest the run may take: a number with a unit, `ms`, `s`, `m`, `h` or `d`.", Example = "\"8h\"", Minimum = 0, ExclusiveMinimum = true, Invariant = "INV-07", Live = true)]
    public TimeSpan Time { get; init; } = TimeSpan.FromHours(8);
}

public enum PermissionMode
{
    Ask,
    Auto,
    ReadOnly,
}
