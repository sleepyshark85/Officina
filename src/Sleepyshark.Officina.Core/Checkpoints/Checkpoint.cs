namespace Sleepyshark.Officina.Core.Checkpoints;

/// <summary>Why a checkpoint was taken (RUN-03). The run's first one, <see cref="Start"/>, and those on demand are always taken.</summary>
public enum CheckpointPoint
{
    /// <summary>After each turn: the default.</summary>
    Turn,

    /// <summary>After each step of a pattern.</summary>
    Step,

    /// <summary>After each integration into the baseline.</summary>
    Integration,

    /// <summary>When the run starts, so a run can always go back to its beginning.</summary>
    Start,

    /// <summary>When the owner or the host asks.</summary>
    OnDemand,
}

/// <summary>An agent's working copy as a checkpoint saved it (DESIGN.md §8).</summary>
/// <param name="TaskId">The working copy's name.</param>
/// <param name="Agent">The agent it belongs to.</param>
/// <param name="Commit">The commit it was at; changes not yet committed were committed first.</param>
public sealed record CopySnapshot(string TaskId, string Agent, string Commit);

/// <summary>
/// The saved state of a run (RUN-03, DESIGN.md §8). History is append-only, so a position in each store is enough: going
/// back truncates to these positions and resets the working copies, and what is restored is byte-identical to what was
/// there, so the history stays valid for the provider.
/// </summary>
/// <param name="RunId">The run.</param>
/// <param name="Number">The checkpoint's place in the run, from 0 for the start.</param>
/// <param name="Time">When it was taken.</param>
/// <param name="Point">Why it was taken.</param>
/// <param name="Conversations">The number of stored turns of each conversation, by agent, for the run's caller.</param>
/// <param name="Record">The run record's revision.</param>
/// <param name="Board">The task board's revision.</param>
/// <param name="Memory">The place in the project memory's log, so the prefix revision and the revision a conversation was told of stay valid (MEM-03).</param>
/// <param name="Audit">How many audit entries the run had, so the effects after the checkpoint can be listed (RUN-08).</param>
/// <param name="Workspace">The working copies.</param>
public sealed record Checkpoint(
    string RunId,
    int Number,
    DateTimeOffset Time,
    CheckpointPoint Point,
    IReadOnlyDictionary<string, int> Conversations,
    long Record,
    long Board,
    long Memory,
    int Audit,
    IReadOnlyList<CopySnapshot> Workspace);
