using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Defaults for every run.</summary>
public sealed record RunDefaults
{
    // INV-07: a run always has a budget.
    [Setting("The run's budget. It can be high, but it cannot be removed or unlimited.",
        Example = """{ "cost": 25, "time": "08:00:00" }""", Live = true)]
    [Required(ErrorMessage = "cannot be removed. Set a limit; it can be high, but it always exists.")]
    public RunBudget Budget { get; init; } = new();

    [Setting("How tool calls that need permission are decided: `ask` the owner about every write tool call no permission rule allows, `auto` by the rules alone, or `readOnly`, where no write tool runs.",
        Example = "\"ask\"", Live = true)]
    public PermissionMode PermissionMode { get; init; } = PermissionMode.Ask;

    // HITL-02.
    [Setting("How long an approval, sign-off or question waits for the owner, as `hh:mm:ss`. With no answer by then, an approval or sign-off is denied and a question goes unanswered.",
        Example = "\"00:30:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = "must be greater than zero.")]
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromMinutes(30);

    // RUN-06.
    [Setting("How long a cancelled agent has to stop, as `hh:mm:ss`. One still running by then is left behind, and its turn ends in a handoff.",
        Example = "\"00:00:10\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = "must be greater than zero.")]
    public TimeSpan CancelWithin { get; init; } = TimeSpan.FromSeconds(10);
}
