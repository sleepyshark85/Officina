using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The limits of one run (RUN-05). Each limit protects INV-07: it can be raised, never removed or zero.</summary>
public sealed record RunBudget
{
    [Setting("The most the run may spend, in USD.", Example = "25", Live = true)]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public decimal Cost { get; init; } = 25m;

    [Setting("The longest the run may take, as `hh:mm:ss` or `d.hh:mm:ss`. Time in which an agent of it waits for the owner, to answer or to resume it, does not count.", Example = "\"08:00:00\"", Live = true)]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public TimeSpan Time { get; init; } = TimeSpan.FromHours(8);

    [Setting("The most tokens the run may use: input, output, cache reads and cache writes together. Unset: only cost and time limit the run.", Example = "500000000", Live = true)]
    [Range(1, long.MaxValue, ErrorMessage = Messages.NotZero)]
    public long? Tokens { get; init; }

    [Setting("The most tool calls the run may make, including tools the provider runs itself. Unset: only cost and time limit the run.", Example = "20000", Live = true)]
    [Range(1, int.MaxValue, ErrorMessage = Messages.NotZero)]
    public int? ToolCalls { get; init; }
}
