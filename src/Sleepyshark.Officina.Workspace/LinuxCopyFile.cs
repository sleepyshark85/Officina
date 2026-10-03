using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Sleepyshark.Officina.Workspace;

/// <summary>
/// On Linux each folder is opened relative to its parent with O_NOFOLLOW, and the file is opened, deleted or moved
/// relative to its open folder (openat, unlinkat, renameat2), so a link swapped in at any moment is refused, not followed.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxCopyFile : CopyFile
{
    // The open flags and error numbers of Linux on x64 and arm64.
    private const int ReadOnly = 0, WriteOnly = 1, Create = 0x40, NonBlocking = 0x800, Folder = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000;
    private const int NoEntry = 2, AlreadyExists = 17, NotFolder = 20, Loop = 40;
    private const int CurrentFolder = -100, NoReplace = 1;

    // Permissions before the umask: read and write, and for folders search, for everyone.
    private const uint AnyoneMayWrite = 0x1B6, Executable = 0x49;

    private readonly SafeFileHandle? folder;
    private readonly string name;
    private readonly string real;

    public LinuxCopyFile(string root, string relative, string path, bool create)
        : base(path)
    {
        var parts = relative.Split('/');
        name = parts[^1];

        // The copy itself is where the host put it, so the path to it may follow links.
        var fd = Native.openat(CurrentFolder, root, ReadOnly | Folder | CloseOnExec, 0);
        var current = fd < 0 ? throw Error() : new SafeFileHandle(fd, ownsHandle: true);
        real = RealPath(current);
        foreach (var part in parts[..^1])
        {
            fd = Native.openat(Fd(current), part, ReadOnly | Folder | NoFollow | CloseOnExec, 0);
            if (fd < 0 && create && Marshal.GetLastPInvokeError() == NoEntry)
            {
                if (Native.mkdirat(Fd(current), part, AnyoneMayWrite | Executable) < 0 && Marshal.GetLastPInvokeError() != AlreadyExists)
                {
                    var failed = Error();
                    current.Dispose();
                    throw failed;
                }

                fd = Native.openat(Fd(current), part, ReadOnly | Folder | NoFollow | CloseOnExec, 0);
            }

            // A link is refused, not followed; this only tells a link from a file for the message.
            var error = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
            var linked = error == NotFolder && new FileInfo($"/proc/self/fd/{Fd(current)}/{part}").LinkTarget is not null;
            current.Dispose();
            if (fd < 0)
            {
                if (error == Loop || linked)
                {
                    throw Outside();
                }

                if (error is NoEntry or NotFolder && !create)
                {
                    // A missing folder holds no file to read.
                    return;
                }

                throw error == NotFolder ? FolderIsFile() : Error(error);
            }

            current = new SafeFileHandle(fd, ownsHandle: true);
            real += "/" + part;
        }

        folder = current;
        if (RealPath(folder) != real)
        {
            folder.Dispose();
            throw Outside();
        }

        real += "/" + name;
    }

    public override async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        using var file = OpenFile(ReadOnly | NonBlocking);
        return file is null || File.GetAttributes(file).HasFlag(FileAttributes.Directory) ? null : await ReadAllAsync(file, ct).ConfigureAwait(false);
    }

    public override async Task WriteAsync(byte[] content, CancellationToken ct)
    {
        using var file = OpenFile(WriteOnly | Create | NonBlocking) ?? throw Error(NoEntry);
        await WriteAllAsync(file, content, ct).ConfigureAwait(false);
    }

    public override void Delete()
    {
        if (folder is null || Native.unlinkat(Fd(folder), name, 0) < 0)
        {
            throw Error();
        }
    }

    public override void MoveTo(CopyFile target)
    {
        var to = (LinuxCopyFile)target;
        if (folder is null || to.folder is null || Native.renameat2(Fd(folder), name, Fd(to.folder), to.name, NoReplace) < 0)
        {
            throw Marshal.GetLastPInvokeError() == AlreadyExists ? to.Exists() : Error();
        }
    }

    public override void Dispose() => folder?.Dispose();

    /// <summary>Opens the file in its folder without following a link, and checks that it is where it was asked for; null if there is no file.</summary>
    private SafeFileHandle? OpenFile(int flags)
    {
        if (folder is null)
        {
            return null;
        }

        var fd = Native.openat(Fd(folder), name, flags | NoFollow | CloseOnExec, AnyoneMayWrite);
        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == NoEntry ? null : throw (error == Loop ? Outside() : Error(error));
        }

        var file = new SafeFileHandle(fd, ownsHandle: true);
        if (RealPath(file) != real)
        {
            file.Dispose();
            throw Outside();
        }

        return file;
    }

    // Where an open file or folder really is, which the kernel keeps up to date as folders are moved.
    private static string RealPath(SafeFileHandle handle) =>
        new FileInfo($"/proc/self/fd/{Fd(handle)}").LinkTarget ?? throw new IOException("The real path of an open file is unknown.");

    private static int Fd(SafeFileHandle handle) => (int)handle.DangerousGetHandle();

    private IOException Error(int? error = null) => new($"{Path}: {Marshal.GetPInvokeErrorMessage(error ?? Marshal.GetLastPInvokeError())}");

    private static partial class Native
    {
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int openat(int folder, string path, int flags, uint mode);

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int mkdirat(int folder, string path, uint mode);

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int unlinkat(int folder, string path, int flags);

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int renameat2(int folder, string path, int newFolder, string newPath, uint flags);
    }
}
