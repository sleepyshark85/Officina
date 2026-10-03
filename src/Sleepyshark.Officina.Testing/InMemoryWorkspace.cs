using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// A workspace held in memory, for tests (TEST-01). Each working copy starts from <see cref="Files"/> as they are then,
/// and keeps its changes to itself until it is integrated. Edits are checked against what the agent last read, as in the git
/// workspace (WS-07); there are no protected paths and no baseline checks. A change to a file that the baseline changed too
/// since the copy started is a conflict (WS-03): the copy then takes the baseline's other changes and keeps its own, as if its
/// author had resolved the conflicts in favour of its change, so the next integration applies.
/// </summary>
public sealed class InMemoryWorkspace : IWorkspace
{
    /// <summary>The baseline: each file's text, by its path.</summary>
    public ConcurrentDictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (Copy Copy, string Agent)> open = new(StringComparer.Ordinal);

    public Task<IWorkingCopy> OpenWorkingCopyAsync(string name, string agent, CancellationToken ct)
    {
        lock (open)
        {
            if (!open.TryGetValue(name, out var found))
            {
                open[name] = found = (new Copy(new(Files)), agent);
            }

            return Task.FromResult<IWorkingCopy>(found.Copy);
        }
    }

    /// <summary>Nothing runs in memory; what the copy holds is its files, sorted, as JSON.</summary>
    public Task<string> SealAsync(IWorkingCopy copy, CancellationToken ct) => Task.FromResult(((Copy)copy).Content());

    public Task<IntegrationResult> IntegrateAsync(IWorkingCopy copy, string task, string author, string? submitted, CancellationToken ct)
    {
        var integrating = (Copy)copy;
        lock (open)
        {
            if (submitted is not null && integrating.Content() != submitted)
            {
                return Task.FromResult(new IntegrationResult(IntegrationOutcome.Changed, []));
            }

            var changed = integrating.Changed();
            var conflicts = changed
                .Where(path => Files.GetValueOrDefault(path) != integrating.Base.GetValueOrDefault(path) && Files.GetValueOrDefault(path) != integrating.Files.GetValueOrDefault(path))
                .Order(StringComparer.Ordinal).ToList();
            if (conflicts.Count > 0)
            {
                integrating.Merge(new(Files));
                return Task.FromResult(new IntegrationResult(IntegrationOutcome.Conflict, conflicts));
            }

            foreach (var path in changed)
            {
                if (integrating.Files.TryGetValue(path, out var text))
                {
                    Files[path] = text;
                }
                else
                {
                    Files.TryRemove(path, out _);
                }
            }

            return Task.FromResult(new IntegrationResult(IntegrationOutcome.Integrated, []));
        }
    }

    /// <summary>What closing a copy throws, as removing a folder the operating system holds on to would; null for nothing.</summary>
    public Exception? CloseFails { get; set; }

    public Task CloseWorkingCopyAsync(IWorkingCopy copy, CancellationToken ct)
    {
        if (CloseFails is { } failure)
        {
            throw failure;
        }

        lock (open)
        {
            foreach (var task in open.Where(found => found.Value.Copy == copy).Select(found => found.Key).ToList())
            {
                open.Remove(task);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>A snapshot's "commit" is the copy's files as JSON, so a snapshot is exact and needs nothing kept.</summary>
    public Task<IReadOnlyList<CopySnapshot>> SnapshotAsync(CancellationToken ct)
    {
        lock (open)
        {
            return Task.FromResult<IReadOnlyList<CopySnapshot>>([.. open.OrderBy(found => found.Key, StringComparer.Ordinal)
                .Select(found => new CopySnapshot(found.Key, found.Value.Agent, JsonSerializer.Serialize(found.Value.Copy.Files)))]);
        }
    }

    public Task RestoreAsync(IReadOnlyList<CopySnapshot> snapshot, CancellationToken ct)
    {
        lock (open)
        {
            var saved = snapshot.Select(copy => copy.TaskId).ToHashSet(StringComparer.Ordinal);
            foreach (var added in open.Keys.Where(task => !saved.Contains(task)).ToList())
            {
                open.Remove(added);
            }

            foreach (var saving in snapshot)
            {
                if (!open.TryGetValue(saving.TaskId, out var found))
                {
                    open[saving.TaskId] = found = (new Copy([]), saving.Agent);
                }

                found.Copy.Reset(JsonSerializer.Deserialize<Dictionary<string, string>>(saving.Commit)!);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>A working copy: its own files, and the text of each file when the agent last read or wrote it.</summary>
    public sealed class Copy(Dictionary<string, string> files) : IWorkingCopy
    {
        private readonly Dictionary<string, string> seen = new(StringComparer.Ordinal);

        /// <summary>The baseline's files when the copy started.</summary>
        internal Dictionary<string, string> Base { get; } = new(files, StringComparer.Ordinal);

        /// <summary>In memory, so there is no folder.</summary>
        public string? Directory => null;

        /// <summary>Takes the baseline as it is now: its changes where the copy has none, and the copy's own where it has.</summary>
        internal void Merge(Dictionary<string, string> baseline)
        {
            lock (files)
            {
                var own = Changed();
                foreach (var path in baseline.Keys.Union(Base.Keys).Except(own).ToList())
                {
                    if (baseline.TryGetValue(path, out var text))
                    {
                        files[path] = text;
                    }
                    else
                    {
                        files.Remove(path);
                    }

                    seen.Remove(path);
                }

                Base.Clear();
                foreach (var (path, text) in baseline)
                {
                    Base[path] = text;
                }
            }
        }

        /// <summary>The paths the copy added, changed or deleted since it started.</summary>
        internal List<string> Changed()
        {
            lock (files)
            {
                return [.. files.Keys.Union(Base.Keys).Where(path => files.GetValueOrDefault(path) != Base.GetValueOrDefault(path))];
            }
        }

        /// <summary>The copy's files, by path.</summary>
        public IReadOnlyDictionary<string, string> Files => files;

        /// <summary>The copy's files, sorted by path, as JSON.</summary>
        internal string Content()
        {
            lock (files)
            {
                return JsonSerializer.Serialize(files.OrderBy(file => file.Key, StringComparer.Ordinal).ToList());
            }
        }

        /// <summary>Puts the copy's files back as they were; the agent has read none of them since.</summary>
        internal void Reset(Dictionary<string, string> saved)
        {
            lock (files)
            {
                files.Clear();
                foreach (var (path, text) in saved)
                {
                    files[path] = text;
                }

                seen.Clear();
            }
        }

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

        public Task DeleteAsync(string path, CancellationToken ct)
        {
            lock (files)
            {
                _ = Unchanged(path) ?? throw new WorkspaceException($"{path} does not exist.");
                files.Remove(path);
                seen.Remove(path);
                return Task.CompletedTask;
            }
        }

        public Task MoveAsync(string path, string newPath, CancellationToken ct)
        {
            lock (files)
            {
                var text = Unchanged(path) ?? throw new WorkspaceException($"{path} does not exist.");
                if (files.ContainsKey(newPath))
                {
                    throw new WorkspaceException($"{newPath} already exists.");
                }

                files.Remove(path);
                seen.Remove(path);
                seen[newPath] = files[newPath] = text;
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
