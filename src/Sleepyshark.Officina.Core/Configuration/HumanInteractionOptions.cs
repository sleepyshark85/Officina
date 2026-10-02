namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Human interaction (HITL): the owner's tool, and the points where a run waits for the owner.</summary>
public sealed record HumanInteractionOptions
{
    [Setting("Whether human interaction is on.", Example = "true")]
    public bool Enabled { get; init; }

    [Setting("Where a run waits for the owner's sign-off: `runBudgetExceeded`, to go on past the run's budget, which otherwise ends the turn; `irreversibleAction`, before every irreversible tool call, whatever the tool's `approval`; and `planApproval`, before a team's work starts on its lead's plan. Unset means the first two. An empty list in a file counts as unset; to turn both off, set an empty list in code.",
        Example = """["runBudgetExceeded"]""")]
    public IReadOnlyList<SignOff>? SignOffs { get; init; }

    /// <summary>Whether a run waits for the owner's sign-off at this point: the configured points, or every one when none are configured.</summary>
    internal bool SignsOff(SignOff point) => Enabled && (SignOffs ?? [SignOff.RunBudgetExceeded, SignOff.IrreversibleAction]).Contains(point);
}

/// <summary>A point where a run waits for the owner (HITL-04).</summary>
public enum SignOff
{
    RunBudgetExceeded,
    IrreversibleAction,
    PlanApproval,
}
