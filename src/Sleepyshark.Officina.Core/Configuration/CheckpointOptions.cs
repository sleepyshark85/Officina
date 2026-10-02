using Sleepyshark.Officina.Core.Checkpoints;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Checkpoints (RUN): saved states a run resumes from after a crash, and the owner can roll back to.</summary>
public sealed record CheckpointOptions
{
    [Setting("Whether checkpoints are on. A run then takes one when it starts, and one at each point in `at`; the owner can take one at any time. A crashed run resumes from its last, and the owner can roll a run back to any. It needs the conversation store.",
        Example = "true")]
    public bool Enabled { get; init; }

    [Setting("Where a checkpoint is taken: `turn` after each turn, `step` after each step of a pattern, `integration` after each integration into the baseline. Unset means `turn`. An empty list in a file counts as unset.",
        Example = """["turn", "integration"]""")]
    public IReadOnlyList<CheckpointPoint>? At { get; init; }

    /// <summary>Whether a checkpoint is taken at this point.</summary>
    internal bool TakenAt(CheckpointPoint point) =>
        Enabled && (point is CheckpointPoint.Start or CheckpointPoint.OnDemand || (At ?? [CheckpointPoint.Turn]).Contains(point));
}
