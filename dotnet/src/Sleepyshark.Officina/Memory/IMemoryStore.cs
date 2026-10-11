namespace Sleepyshark.Officina;

/// <summary>
/// Where memory files are kept. Every operation is within one scope, which never sees another's files. A store refuses
/// any scope or path <see cref="MemoryPath"/> rejects, with an <see cref="ArgumentException"/>, so no path leaves its
/// scope. Directories are implied by the files' paths.
/// </summary>
public interface IMemoryStore
{
    /// <summary>Every file of the scope, in any order; none for a scope never written to.</summary>
    Task<IReadOnlyList<MemoryFile>> ListAsync(string scope, CancellationToken cancellationToken);

    /// <summary>The file's text; null when there is no such file.</summary>
    Task<string?> ReadAsync(string scope, string path, CancellationToken cancellationToken);

    /// <summary>Creates the file, or replaces its text.</summary>
    Task WriteAsync(string scope, string path, string content, CancellationToken cancellationToken);

    /// <summary>Deletes the file; does nothing when there is none.</summary>
    Task DeleteAsync(string scope, string path, CancellationToken cancellationToken);

    /// <summary>Moves the file; throws when it does not exist, or when <paramref name="newPath"/> does.</summary>
    Task RenameAsync(string scope, string path, string newPath, CancellationToken cancellationToken);
}
