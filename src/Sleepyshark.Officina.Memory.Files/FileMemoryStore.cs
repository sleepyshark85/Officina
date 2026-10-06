using System.Text;

namespace Sleepyshark.Officina.Memory.Files;

/// <summary>
/// Keeps memory files on disk: each scope is a directory under <paramref name="root"/>, named by the hex of its UTF-8
/// bytes so scopes differing in case stay apart, with UTF-8 text files under it. Beyond <see cref="MemoryPath"/>'s
/// checks, a path must resolve inside its scope's directory, and neither that directory nor any part of the path may be
/// a link (symbolic link or junction), so no file outside the scope is reached; files reached only through a link are
/// not listed. Directories left empty are removed.
/// </summary>
/// <remarks>
/// It guards against the model's paths, not other processes changing the directory. On a case-insensitive file system,
/// paths in one scope that differ only in case name the same file. Directory names are twice the scope's UTF-8 bytes
/// and file systems cap names at 255 characters, so a scope over 127 UTF-8 bytes cannot hold files, although
/// <see cref="MemoryPath"/> accepts it.
/// </remarks>
public sealed class FileMemoryStore(string root) : IMemoryStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));

    public Task<IReadOnlyList<MemoryFile>> ListAsync(string scope, CancellationToken cancellationToken)
    {
        var directory = Directory(scope);
        if (!System.IO.Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<MemoryFile>>([]);
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        return Task.FromResult<IReadOnlyList<MemoryFile>>([
            .. new DirectoryInfo(directory).EnumerateFiles("*", options)
                .Select(file => new MemoryFile(Path.GetRelativePath(directory, file.FullName).Replace(Path.DirectorySeparatorChar, '/'), file.Length))
                .Where(file => MemoryPath.IsValid(file.Path)),
        ]);
    }

    public async Task<string?> ReadAsync(string scope, string path, CancellationToken cancellationToken)
    {
        var file = File(scope, path);
        return System.IO.File.Exists(file) ? await System.IO.File.ReadAllTextAsync(file, Utf8, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task WriteAsync(string scope, string path, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var file = File(scope, path);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await System.IO.File.WriteAllTextAsync(file, content, Utf8, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string scope, string path, CancellationToken cancellationToken)
    {
        var file = File(scope, path);
        if (System.IO.File.Exists(file))
        {
            System.IO.File.Delete(file);
            Prune(scope, file);
        }

        return Task.CompletedTask;
    }

    public Task RenameAsync(string scope, string path, string newPath, CancellationToken cancellationToken)
    {
        var (from, to) = (File(scope, path), File(scope, newPath));
        if (!System.IO.File.Exists(from))
        {
            throw new FileNotFoundException($"There is no file {path}.");
        }

        if (System.IO.File.Exists(to) || System.IO.Directory.Exists(to))
        {
            throw new IOException($"There is already a file or directory {newPath}.");
        }

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        System.IO.File.Move(from, to);
        Prune(scope, from);
        return Task.CompletedTask;
    }

    /// <summary>The scope's directory, which must not be a link.</summary>
    private string Directory(string scope)
    {
        MemoryPath.Check(scope);
        var directory = Path.Combine(root, Convert.ToHexStringLower(Encoding.UTF8.GetBytes(scope)));
        RefuseLink(directory, scope);
        return directory;
    }

    /// <summary>The file at <paramref name="path"/> in the scope's directory, reached through no link.</summary>
    private string File(string scope, string path)
    {
        MemoryPath.Check(scope, path);
        var directory = Directory(scope);
        var file = Path.GetFullPath(Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{path}' is outside the memory scope.", nameof(path));
        }

        for (var at = file; at.Length > directory.Length; at = Path.GetDirectoryName(at)!)
        {
            RefuseLink(at, path);
        }

        return file;
    }

    private static void RefuseLink(string at, string name)
    {
        if (new FileInfo(at).LinkTarget is not null)
        {
            throw new IOException($"'{name}' leads through a link, which memory does not follow.");
        }
    }

    /// <summary>Removes the directories above <paramref name="file"/> left empty, up to the scope's.</summary>
    private void Prune(string scope, string file)
    {
        var directory = Directory(scope);
        for (var at = Path.GetDirectoryName(file)!; at.Length > directory.Length && !System.IO.Directory.EnumerateFileSystemEntries(at).Any(); at = Path.GetDirectoryName(at)!)
        {
            System.IO.Directory.Delete(at);
        }
    }
}
