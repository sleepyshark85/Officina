using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>When a turn is complete (LOOP-05). They combine: the first that holds completes the turn.</summary>
public sealed record StopConditions
{
    [Setting("The turn completes when the model finishes its reply. When this is `false` and the model finishes, the turn ends in a handoff.",
        Example = "false")]
    public bool Finished { get; init; } = true;

    [Setting("A tool, by name in `tools`, that completes the turn when a call of it succeeds. The call's arguments are the output.",
        Example = "\"submit_report\"")]
    public string? FinishTool { get; init; }

    [Setting("The turn completes after this many model calls, with the model's last text as the output.", Example = "1")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int? MaxIterations { get; init; }
}
