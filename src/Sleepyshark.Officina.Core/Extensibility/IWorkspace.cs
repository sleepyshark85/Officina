using Sleepyshark.Officina.Core.Checkpoints;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// Where agents change files: a working copy for each agent's task (WS-01). The <c>workspace.*</c> tools work through
/// it, so the test kit's in-memory workspace can stand in for the git workspace (TEST-01). Checkpoints save and
/// restore its working copies (RUN-04, RUN-08). A task's verified change reaches the baseline through integration (WS-09).
/// </summary>
public interface IWorkspace
{
    /// <summary>
    /// Creates the working copy where an agent does a task, from the baseline as it is now. A working copy that is already
    /// open for the task is returned as it is, and so is one that a restore brought back.
    /// </summary>
    Task<IWorkingCopy> OpenWorkingCopyAsync(string taskId, string agent, CancellationToken ct);

    /// <summary>Removes the working copy when its task ends.</summary>
    Task CloseWorkingCopyAsync(IWorkingCopy copy, CancellationToken ct);

    /// <summary>
    /// Integrates the working copy's changes into the baseline, through the workspace's one queue, in order (WS-09): applied to the
    /// baseline as it is when the change's turn comes, and only if the baseline's checks still pass then (WS-02). A change that no
    /// longer applies is a conflict, never resolved silently (WS-03): the copy is left with the conflicts for its author to resolve.
    /// </summary>
    /// <param name="copy">The working copy.</param>
    /// <param name="task">The task the change is for, which the baseline's history names (WS-04).</param>
    /// <param name="author">The agent that made the change.</param>
    /// <param name="ct">Cancels the integration.</param>
    Task<IntegrationResult> IntegrateAsync(IWorkingCopy copy, string task, string author, CancellationToken ct);

    /// <summary>Saves every open working copy as it is now, committing changes not yet committed first (DESIGN.md §8).</summary>
    Task<IReadOnlyList<CopySnapshot>> SnapshotAsync(CancellationToken ct);

    /// <summary>
    /// Returns the working copies to a snapshot: each one in it is reset to its saved state, opened again if it is gone, and
    /// any other open working copy is removed, because it did not exist then (RUN-04, RUN-08).
    /// </summary>
    Task RestoreAsync(IReadOnlyList<CopySnapshot> snapshot, CancellationToken ct);
}

/// <summary>
/// One agent's copy of the workspace. Agents reach files only through it: only inside the copy and never protected paths
/// (WS-05), in parts and by search (WS-06), and an edit fails if the file changed since the agent read it (WS-07). A
/// refused operation throws <see cref="WorkspaceException"/>.
/// </summary>
public interface IWorkingCopy
{
    /// <summary>The copy's folder, where checks run their commands (WS-02); null for a copy that is not on disk.</summary>
    string? Directory { get; }

    /// <summary>Reads a file, or only some of its lines.</summary>
    /// <param name="path">The file, relative to the copy.</param>
    /// <param name="firstLine">The first line to read, from 1; null reads the whole file.</param>
    /// <param name="lineCount">How many lines to read from <paramref name="firstLine"/>; null reads to the end.</param>
    /// <param name="ct">Cancels the read.</param>
    Task<string> ReadAsync(string path, int? firstLine, int? lineCount, CancellationToken ct);

    /// <summary>The lines that match a regular expression, in every file the agent can see.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(string pattern, CancellationToken ct);

    /// <summary>Replaces the one place where <paramref name="oldText"/> occurs, in a file the agent has read since it last changed.</summary>
    Task EditAsync(string path, string oldText, string newText, CancellationToken ct);

    /// <summary>Creates a file, or replaces one the agent has read since it last changed.</summary>
    Task WriteAsync(string path, string content, CancellationToken ct);

    /// <summary>Deletes a file the agent has read since it last changed.</summary>
    Task DeleteAsync(string path, CancellationToken ct);

    /// <summary>Moves a file the agent has read since it last changed, to a path where no file exists.</summary>
    Task MoveAsync(string path, string newPath, CancellationToken ct);
}

/// <summary>A line that matched a search.</summary>
/// <param name="Path">The file, relative to the working copy.</param>
/// <param name="Line">The line number, from 1.</param>
/// <param name="Text">The line.</param>
public sealed record SearchHit(string Path, int Line, string Text);

/// <summary>How an integration ended.</summary>
/// <param name="Outcome">Whether the change reached the baseline.</param>
/// <param name="Details">For a conflict, the conflicting files; for failed checks, each finding after its check's name.</param>
public sealed record IntegrationResult(IntegrationOutcome Outcome, IReadOnlyList<string> Details);

public enum IntegrationOutcome
{
    /// <summary>The baseline now has the change.</summary>
    Integrated,

    /// <summary>The change no longer applies to the baseline; it goes back to the author or the lead (WS-03).</summary>
    Conflict,

    /// <summary>A baseline check failed on the change, so the baseline did not move (WS-02).</summary>
    ChecksFailed,
}

/// <summary>
/// The names of a run's working copies, which the host and the core both use: one per task, which every agent working on or
/// reviewing the task shares, and one per agent for work that is for no task (WS-01).
/// </summary>
public static partial class WorkingCopies
{
    /// <summary>The working copy of a task of a run.</summary>
    public static string OfTask(string runId, string taskId) => $"{runId}-task.{Safe(taskId)}"; // no agent's name has a dot

    /// <summary>The working copy of an agent's work that is for no task, by its id in the run.</summary>
    public static string OfAgent(string runId, string agentId) => $"{runId}-{Safe(agentId)}";

    /// <summary>A name as part of a git branch and a folder name; a name with other characters gets a hash, so two such names stay apart.</summary>
    private static string Safe(string name) =>
        SafeCharacters().IsMatch(name)
            ? name
            : $"{UnsafeCharacters().Replace(name, "_")}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..6]}";

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial System.Text.RegularExpressions.Regex SafeCharacters();

    [System.Text.RegularExpressions.GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial System.Text.RegularExpressions.Regex UnsafeCharacters();
}
