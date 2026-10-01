using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The limits of one run (RUN-05). Each limit protects INV-07: it can be raised, never removed or zero.</summary>
public sealed record RunBudget
{
    [Setting("The most the run may spend, in USD.", Example = "25", Live = true)]
    [Range(typeof(decimal), "0", "79228162514264337593543950335", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public decimal Cost { get; init; } = 25m;

    [Setting("The longest the run may take, as `hh:mm:ss` or `d.hh:mm:ss`.", Example = "\"08:00:00\"", Live = true)]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Messages.NotZero)]
    public TimeSpan Time { get; init; } = TimeSpan.FromHours(8);
}
