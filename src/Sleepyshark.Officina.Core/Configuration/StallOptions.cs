using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>When a turn has stalled (LOOP-07).</summary>
public sealed record StallOptions
{
    [Setting("A turn ends in a handoff after this many tool-calling iterations in a row that only repeat earlier calls and get the same results.",
        Example = "5")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int IterationsWithoutProgress { get; init; } = 3;
}
