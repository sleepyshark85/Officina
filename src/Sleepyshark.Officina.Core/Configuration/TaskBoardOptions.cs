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

    [Setting("What a task's turns may spend together. A task whose budget is used up goes back to the lead.",
        Example = """{ "cost": 8, "tokens": 20000000, "toolCalls": 500, "time": "02:00:00" }""")]
    public TaskBudget Budget { get; init; } = new();
}

/// <summary>What a task's turns may spend together (RUN-05, TASK-09). Its cost always has a limit; the others only when set.</summary>
public sealed record TaskBudget
{
    [Setting("The most a task's turns may cost, in USD, unless the owner gives the task another budget.", Example = "8")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public decimal Cost { get; init; } = 8m;

    [Setting("The most tokens a task's turns may use: input, output, cache reads and cache writes together. Unset means no limit of its own.", Example = "20000000")]
    [Range(1, long.MaxValue, ErrorMessage = Messages.NotZero)]
    public long? Tokens { get; init; }

    [Setting("The most tool calls a task's turns may make. Unset means no limit of its own.", Example = "500")]
    [Range(1, int.MaxValue, ErrorMessage = Messages.NotZero)]
    public int? ToolCalls { get; init; }

    [Setting("The longest a task's turns may take together, as `hh:mm:ss`, less the time they wait for the owner. Unset means no limit of its own.", Example = "\"02:00:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public TimeSpan? Time { get; init; }
}
