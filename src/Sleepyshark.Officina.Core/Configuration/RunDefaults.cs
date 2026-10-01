namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Defaults for every run. An agent's own settings override them for that agent.</summary>
public sealed record RunDefaults
{
    [Setting("The run's budget. Every run has one; it can be high, but never unlimited (RUN-05, INV-07).", Example = "{ \"cost\": 25, \"time\": \"8h\" }", Invariant = "INV-07", Live = true)]
    public RunBudget Budget { get; init; } = new();

    [Setting("How tool calls that need permission are decided: `ask` the owner, decide `auto`matically by the rules, or allow `readOnly` tools only (HITL-01).", Example = "\"ask\"", Live = true)]
    public PermissionMode PermissionMode { get; init; } = PermissionMode.Ask;
}

/// <summary>The limits of one run. The owner may raise them during a run, never lower them away (CFG-08).</summary>
public sealed record RunBudget
{
    [Setting("The most the run may spend, in the currency of the price table (USD by default).", Example = "25", Minimum = 0, ExclusiveMinimum = true, Invariant = "INV-07", Live = true)]
    public decimal Cost { get; init; } = 25m;

    [Setting("The longest the run may take.", Example = "\"8h\"", Minimum = 0, ExclusiveMinimum = true, Invariant = "INV-07", Live = true)]
    public TimeSpan Time { get; init; } = TimeSpan.FromHours(8);
}

public enum PermissionMode
{
    Ask,
    Auto,
    ReadOnly,
}
