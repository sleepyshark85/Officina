using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The team (TEAM): agents of several roles over the task board, and the helpers agents start (TEAM-07).</summary>
public sealed record TeamOptions
{
    [Setting("Whether the team is on.", Example = "true")]
    public bool Enabled { get; init; }

    [Setting("How deep helpers may go: a helper of a helper is depth 2.", Example = "1")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int HelperDepth { get; init; } = 2;

    [Setting("The most helpers one turn of an agent may start.", Example = "2")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int HelperCount { get; init; } = 4;
}
