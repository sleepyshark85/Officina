using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Workspace;

/// <summary>
/// One agent's copy of the workspace: a git worktree on its own branch, so its changes are invisible to other agents
/// until they are integrated (WS-01). Agents reach files only through it: only inside the copy and never protected
/// paths (WS-05), in parts and by search (WS-06), and an edit fails if the file changed since the agent read it (WS-07).
/// </summary>
public sealed class WorkingCopy : IWorkingCopy
{
    private readonly Matcher hidden;
    private readonly Matcher readOnly;

    // The content hash of each file when the agent last read or wrote it (WS-07). The copy has one agent, so one map.
    private readonly ConcurrentDictionary<string, string> seen = new(StringComparer.Ordinal);

    internal WorkingCopy(string taskId, string agent, string directory, Matcher hidden, Matcher readOnly)
    {
        TaskId = taskId;
        Agent = agent;
        Directory = directory;
        this.hidden = hidden;
        this.readOnly = readOnly;
    }

    public string TaskId { get; }

    public string Agent { get; }

    /// <summary>Where the copy is on disk, for the sandbox to run commands in (S15).</summary>
    public string Directory { get; }

    internal string Branch => $"agent/{TaskId}";

    /// <summary>Reads a file, or only some of its lines, so a large file need not enter the conversation whole (WS-06).</summary>
    /// <param name="path">The file, relative to the copy.</param>
    /// <param name="firstLine">The first line to read, from 1; null reads the whole file.</param>
    /// <param name="lineCount">How many lines to read from <paramref name="firstLine"/>; null reads to the end.</param>
    /// <param name="ct">Cancels the read.</param>
    public async Task<string> ReadAsync(string path, int? firstLine = null, int? lineCount = null, CancellationToken ct = default)
    {
        var (full, relative) = Resolve(path, change: false);
        var text = await ReadTextAsync(full, relative, path, ct).ConfigureAwait(false);
        if (firstLine is null)
        {
            return text;
        }

        var lines = text.Split('\n').Skip(Math.Max(firstLine.Value, 1) - 1);
        return string.Join('\n', lineCount is null ? lines : lines.Take(lineCount.Value));
    }

    /// <summary>The lines that match a regular expression, in every file the agent can see (WS-06).</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string pattern, CancellationToken ct = default)
    {
        var (exitCode, output, error) = await Git.TryRunAsync(
            Directory, ct, "grep", "--untracked", "-I", "-n", "-z", "--no-color", "-E", "-e", pattern).ConfigureAwait(false);
        if (exitCode > 1)
        {
            throw new WorkspaceException($"The search failed: {error.Trim()}");
        }

        // With -z each match is "path NUL line NUL text".
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(match => match.Split('\0', 3))
            .Where(parts => !hidden.Match(parts[0]).HasMatches)
            .Select(parts => new SearchHit(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2]))];
    }

    /// <summary>
    /// Replaces the one place where <paramref name="oldText"/> occurs (WS-07). It fails if the agent has not read the file
    /// since it last changed, so an edit never overwrites a change the agent has not seen.
    /// </summary>
    public async Task EditAsync(string path, string oldText, string newText, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldText);
        var (full, relative) = Resolve(path, change: true);
        var text = await ReadTextAsync(full, relative, path, ct, check: true).ConfigureAwait(false);
        var at = text.IndexOf(oldText, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(oldText, at + 1, StringComparison.Ordinal) >= 0)
        {
            throw new WorkspaceException($"The text to replace must occur exactly once in {path}, but it occurs {(at < 0 ? "nowhere" : "more than once")}.");
        }

        await WriteTextAsync(full, relative, string.Concat(text.AsSpan(0, at), newText, text.AsSpan(at + oldText.Length)), ct).ConfigureAwait(false);
    }

    /// <summary>Creates a file, or replaces one the agent has read since it last changed (WS-07).</summary>
    public async Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        var (full, relative) = Resolve(path, change: true);
        if (File.Exists(full))
        {
            await ReadTextAsync(full, relative, path, ct, check: true).ConfigureAwait(false);
        }

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await WriteTextAsync(full, relative, content, ct).ConfigureAwait(false);
    }

    /// <summary>The full path of a file the agent may reach, and its path relative to the copy with '/' separators.</summary>
    private (string Full, string Relative) Resolve(string path, bool change)
    {
        var full = Path.GetFullPath(path, Directory);
        var relative = Path.GetRelativePath(Directory, full).Replace('\\', '/');
        if (relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) || Linked(full))
        {
            throw new WorkspaceException($"{path} is outside the workspace.");
        }

        if (hidden.Match(relative).HasMatches)
        {
            throw NotFound(path);
        }

        return change && readOnly.Match(relative).HasMatches ? throw new WorkspaceException($"{path} is read-only.") : (full, relative);
    }

    /// <summary>Whether the path, or a folder on the way to it, is a link, which could lead outside the copy.</summary>
    private bool Linked(string full)
    {
        for (var path = full; path.Length > Directory.Length; path = Path.GetDirectoryName(path)!)
        {
            if (Path.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads a file and remembers its hash; with <paramref name="check"/>, first requires it unchanged since the agent last saw it.</summary>
    private async Task<string> ReadTextAsync(string full, string relative, string path, CancellationToken ct, bool check = false)
    {
        if (!File.Exists(full))
        {
            throw NotFound(path);
        }

        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (check && (!seen.TryGetValue(relative, out var known) || known != hash))
        {
            throw new WorkspaceException($"{path} changed since you last read it, or you have not read it. Read it again first.");
        }

        seen[relative] = hash;
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task WriteTextAsync(string full, string relative, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await File.WriteAllBytesAsync(full, bytes, ct).ConfigureAwait(false);
        seen[relative] = Convert.ToHexString(SHA256.HashData(bytes));
    }

    // A hidden file is reported like a missing one, so agents cannot learn that it exists.
    private static WorkspaceException NotFound(string path) => new($"{path} does not exist.");
}
