using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The task board (TASK).</summary>
public sealed record TaskBoardOptions
{
    [Setting("Whether the task board is on.", Example = "true")]
    public bool Enabled { get; init; }

    [Setting("How many attempts of a task may fail a check, a review or an integration before it goes back to the lead.", Example = "3")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int MaxAttempts { get; init; } = 3;

    [Setting("The most a task's turns may cost, in USD, unless the owner gives the task another budget. A task whose budget is used up goes back to the lead.",
        Example = "8")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public decimal Budget { get; init; } = 8m;
}
