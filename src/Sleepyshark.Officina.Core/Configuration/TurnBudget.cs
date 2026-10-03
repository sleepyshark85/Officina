using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The limits of one turn (LOOP-06).</summary>
public sealed record TurnBudget
{
    [Setting("The most model calls in a turn.", Example = "50")]
    [Range(1, int.MaxValue, ErrorMessage = Messages.NotZero)]
    public int Iterations { get; init; } = 50;

    [Setting("The most tool calls in a turn, including tools the provider runs itself.", Example = "200")]
    [Range(1, int.MaxValue, ErrorMessage = Messages.NotZero)]
    public int ToolCalls { get; init; } = 200;

    [Setting("The most tokens a turn may use: input, output, cache reads and cache writes together.", Example = "3000000")]
    [Range(1, long.MaxValue, ErrorMessage = Messages.NotZero)]
    public long Tokens { get; init; } = 3_000_000;

    [Setting("The most a turn may spend, in USD.", Example = "5")]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public decimal Cost { get; init; } = 5m;

    [Setting("The longest a turn may take, as `hh:mm:ss`. Time waiting for the owner, to answer or to resume it, does not count.", Example = "\"00:45:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public TimeSpan Time { get; init; } = TimeSpan.FromMinutes(45);
}
