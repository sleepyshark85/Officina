using Microsoft.Win32.SafeHandles;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Workspace;

/// <summary>
/// A file in a working copy, reached so that no link can lead outside the copy (WS-05). Processes in the sandbox change
/// the copy while the host reads and writes it, so checking a path for links and then opening it would leave a moment
/// in which a folder on the way could become a link. Instead the file's folders are opened one by one, never following
/// a link, and held open while the file is used; the opened file's real path is then checked to be the one asked for.
/// </summary>
internal abstract class CopyFile : IDisposable
{
    /// <summary>The file as the agent named it, for messages.</summary>
    protected CopyFile(string path) => Path = path;

    protected string Path { get; }

    /// <summary>Opens the folders on the way to a file in the copy.</summary>
    /// <param name="root">The copy.</param>
    /// <param name="relative">The file, relative to the copy with '/' separators.</param>
    /// <param name="path">The file as the agent named it, for messages.</param>
    /// <param name="create">Whether to create missing folders, for a file about to be written.</param>
    public static CopyFile Open(string root, string relative, string path, bool create) =>
        OperatingSystem.IsWindows() ? new WindowsCopyFile(root, relative, path, create)
        : OperatingSystem.IsLinux() ? new LinuxCopyFile(root, relative, path, create)
        : throw new PlatformNotSupportedException("Working copies need Linux or Windows.");

    /// <summary>The file's content, or null if there is no file.</summary>
    public abstract Task<byte[]?> ReadAsync(CancellationToken ct);

    /// <summary>Creates or replaces the file.</summary>
    public abstract Task WriteAsync(byte[] content, CancellationToken ct);

    public abstract void Delete();

    /// <summary>Moves the file to where no file exists.</summary>
    public abstract void MoveTo(CopyFile target);

    public abstract void Dispose();

    protected WorkspaceException Outside() => new($"{Path} is outside the workspace.");

    protected WorkspaceException Exists() => new($"{Path} already exists.");

    protected WorkspaceException FolderIsFile() => new($"A folder on the way to {Path} is a file.");

    protected static async Task<byte[]> ReadAllAsync(SafeFileHandle file, CancellationToken ct)
    {
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        for (int read; (read = await RandomAccess.ReadAsync(file, buffer, content.Length, ct).ConfigureAwait(false)) > 0;)
        {
            content.Write(buffer, 0, read);
        }

        return content.ToArray();
    }

    protected static async Task WriteAllAsync(SafeFileHandle file, byte[] content, CancellationToken ct)
    {
        await RandomAccess.WriteAsync(file, content, 0, ct).ConfigureAwait(false);
        RandomAccess.SetLength(file, content.Length);
    }
}
