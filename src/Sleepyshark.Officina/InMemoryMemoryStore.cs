using System.Text;

namespace Sleepyshark.Officina;

/// <summary>
/// A memory store that keeps its files in memory, for tests, demos and short-lived agents: everything is gone when the
/// process ends. Safe for concurrent runs.
/// </summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(string Scope, string Path), string> files = [];

    public Task<IReadOnlyList<MemoryFile>> ListAsync(string scope, CancellationToken cancellationToken)
    {
        MemoryPath.Check(scope);
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<MemoryFile>>(
                [.. files.Where(file => file.Key.Scope == scope).Select(file => new MemoryFile(file.Key.Path, Encoding.UTF8.GetByteCount(file.Value)))]);
        }
    }

    public Task<string?> ReadAsync(string scope, string path, CancellationToken cancellationToken)
    {
        MemoryPath.Check(scope, path);
        lock (gate)
        {
            return Task.FromResult(files.GetValueOrDefault((scope, path)));
        }
    }

    public Task WriteAsync(string scope, string path, string content, CancellationToken cancellationToken)
    {
        MemoryPath.Check(scope, path);
        ArgumentNullException.ThrowIfNull(content);
        lock (gate)
        {
            files[(scope, path)] = content;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string scope, string path, CancellationToken cancellationToken)
    {
        MemoryPath.Check(scope, path);
        lock (gate)
        {
            files.Remove((scope, path));
        }

        return Task.CompletedTask;
    }

    public Task RenameAsync(string scope, string path, string newPath, CancellationToken cancellationToken)
    {
        MemoryPath.Check(scope, path);
        MemoryPath.Check(scope, newPath);
        lock (gate)
        {
            if (files.ContainsKey((scope, newPath)))
            {
                throw new IOException($"There is already a file {newPath}.");
            }

            if (!files.Remove((scope, path), out var content))
            {
                throw new FileNotFoundException($"There is no file {path}.");
            }

            files[(scope, newPath)] = content;
        }

        return Task.CompletedTask;
    }
}
