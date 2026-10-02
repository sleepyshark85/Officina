using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// A workspace held in memory, for tests (TEST-01). Each working copy starts from <see cref="Files"/> as they are then,
/// and keeps its changes to itself. Edits are checked against what the agent last read, as in the git workspace (WS-07);
/// there are no protected paths.
/// </summary>
public sealed class InMemoryWorkspace : IWorkspace
{
    /// <summary>The baseline: each file's text, by its path.</summary>
    public ConcurrentDictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public Task<IWorkingCopy> OpenWorkingCopyAsync(string taskId, string agent, CancellationToken ct) => Task.FromResult<IWorkingCopy>(new Copy(new(Files)));

    public Task CloseWorkingCopyAsync(IWorkingCopy copy, CancellationToken ct) => Task.CompletedTask;

    /// <summary>A working copy: its own files, and the text of each file when the agent last read or wrote it.</summary>
    public sealed class Copy(Dictionary<string, string> files) : IWorkingCopy
    {
        private readonly Dictionary<string, string> seen = new(StringComparer.Ordinal);

        /// <summary>The copy's files, by path.</summary>
        public IReadOnlyDictionary<string, string> Files => files;

        public Task<string> ReadAsync(string path, int? firstLine, int? lineCount, CancellationToken ct)
        {
            lock (files)
            {
                var text = files.TryGetValue(path, out var found) ? found : throw new WorkspaceException($"{path} does not exist.");
                seen[path] = text;
                var lines = text.Split('\n').Skip((firstLine ?? 1) - 1);
                return Task.FromResult(firstLine is null ? text : string.Join('\n', lineCount is null ? lines : lines.Take(lineCount.Value)));
            }
        }

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string pattern, CancellationToken ct)
        {
            lock (files)
            {
                var regex = new Regex(pattern);
                return Task.FromResult<IReadOnlyList<SearchHit>>([.. files.OrderBy(file => file.Key, StringComparer.Ordinal)
                    .SelectMany(file => file.Value.Split('\n').Select((line, index) => new SearchHit(file.Key, index + 1, line)))
                    .Where(hit => regex.IsMatch(hit.Text))]);
            }
        }

        public Task EditAsync(string path, string oldText, string newText, CancellationToken ct)
        {
            lock (files)
            {
                var text = Unchanged(path) ?? throw new WorkspaceException($"{path} does not exist.");
                var at = text.IndexOf(oldText, StringComparison.Ordinal);
                if (at < 0 || text.IndexOf(oldText, at + 1, StringComparison.Ordinal) >= 0)
                {
                    throw new WorkspaceException($"The text to replace must occur exactly once in {path}.");
                }

                seen[path] = files[path] = string.Concat(text.AsSpan(0, at), newText, text.AsSpan(at + oldText.Length));
                return Task.CompletedTask;
            }
        }

        public Task WriteAsync(string path, string content, CancellationToken ct)
        {
            lock (files)
            {
                Unchanged(path);
                seen[path] = files[path] = content;
                return Task.CompletedTask;
            }
        }

        /// <summary>The file's text, which must be what the agent last saw; null when there is no such file.</summary>
        private string? Unchanged(string path) =>
            !files.TryGetValue(path, out var text) ? null
            : seen.TryGetValue(path, out var known) && known == text ? text
            : throw new WorkspaceException($"{path} changed since you last read it, or you have not read it. Read it again first.");
    }
}
