namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// Where agents change files: a working copy for each agent's task (WS-01). The <c>workspace.*</c> tools work through
/// it, so the test kit's in-memory workspace can stand in for the git workspace (TEST-01). The task board (S18) adds
/// integration, and checkpoints (S19) snapshots.
/// </summary>
public interface IWorkspace
{
    /// <summary>Creates the working copy where an agent does a task, from the baseline as it is now.</summary>
    Task<IWorkingCopy> OpenWorkingCopyAsync(string taskId, string agent, CancellationToken ct);

    /// <summary>Removes the working copy when its task ends.</summary>
    Task CloseWorkingCopyAsync(IWorkingCopy copy, CancellationToken ct);
}

/// <summary>
/// One agent's copy of the workspace. Agents reach files only through it: only inside the copy and never protected paths
/// (WS-05), in parts and by search (WS-06), and an edit fails if the file changed since the agent read it (WS-07). A
/// refused operation throws <see cref="WorkspaceException"/>.
/// </summary>
public interface IWorkingCopy
{
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
}

/// <summary>A line that matched a search.</summary>
/// <param name="Path">The file, relative to the working copy.</param>
/// <param name="Line">The line number, from 1.</param>
/// <param name="Text">The line.</param>
public sealed record SearchHit(string Path, int Line, string Text);
