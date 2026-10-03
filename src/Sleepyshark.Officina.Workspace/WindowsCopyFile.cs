using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Sleepyshark.Officina.Workspace;

/// <summary>
/// On Windows each folder on the way to the file is opened as itself, not as what a link or junction leads to, and held
/// open without sharing delete access, so no one can rename, delete or replace it with a link until the file is used.
/// The file is opened as itself too, and refused if it is a link or its final path is not the one asked for.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCopyFile : CopyFile
{
    private const uint ListFolder = 0x1, ReadAttributes = 0x80, GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    private const int FileNotFound = 2, PathNotFound = 3, AccessDenied = 5;

    private readonly List<SafeFileHandle> folders = [];
    private readonly string full;
    private readonly string real;
    private readonly bool missing;

    public WindowsCopyFile(string root, string relative, string path, bool create)
        : base(path)
    {
        var parts = relative.Split('/');
        full = System.IO.Path.Combine([root, .. parts]);
        try
        {
            // The copy itself is where the host put it, so the path to it may follow links.
            var folder = root;
            var handle = OpenFolder(folder, followLink: true) ?? throw Error();
            real = FinalPath(handle);
            foreach (var part in parts[..^1])
            {
                folder = System.IO.Path.Combine(folder, part);
                if (create && !System.IO.Path.Exists(folder))
                {
                    // The folder it is created in is held open, so it cannot be anywhere else.
                    System.IO.Directory.CreateDirectory(folder);
                }

                handle = OpenFolder(folder, followLink: false);
                if (handle is null)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is FileNotFound or PathNotFound && !create)
                    {
                        // A missing folder holds no file to read.
                        missing = true;
                        return;
                    }

                    throw Error(error);
                }

                var attributes = File.GetAttributes(handle);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Outside();
                }

                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    if (create)
                    {
                        throw FolderIsFile();
                    }

                    missing = true;
                    return;
                }

                real += "\\" + part;
            }

            if (!string.Equals(FinalPath(handle), real, StringComparison.OrdinalIgnoreCase))
            {
                throw Outside();
            }

            real += "\\" + parts[^1];
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public override async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        if (missing)
        {
            return null;
        }

        using var file = OpenFile(GenericRead, FileShare.ReadWrite | FileShare.Delete, FileMode.Open);
        return file is null ? null : await ReadAllAsync(file, ct).ConfigureAwait(false);
    }

    public override async Task WriteAsync(byte[] content, CancellationToken ct)
    {
        using var file = OpenFile(GenericWrite, FileShare.Read, FileMode.OpenOrCreate) ?? throw Error();
        await WriteAllAsync(file, content, ct).ConfigureAwait(false);
    }

    // The folders are held open, and deleting or moving a link affects the link, not what it leads to.
    public override void Delete() => File.Delete(full);

    public override void MoveTo(CopyFile target)
    {
        var to = (WindowsCopyFile)target;
        if (System.IO.Path.Exists(to.full))
        {
            throw to.Exists();
        }

        File.Move(full, to.full);
    }

    public override void Dispose()
    {
        foreach (var folder in folders)
        {
            folder.Dispose();
        }
    }

    /// <summary>Opens a folder and holds it open, so that it cannot be renamed, deleted or replaced; null if it cannot be opened.</summary>
    private SafeFileHandle? OpenFolder(string path, bool followLink)
    {
        // Windows applies a handle's sharing only if it was opened to read, write or delete, so it lists the folder.
        var handle = Native.CreateFileW(path, ListFolder | ReadAttributes, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, BackupSemantics | (followLink ? 0 : OpenReparsePoint), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            Marshal.SetLastPInvokeError(error);
            return null;
        }

        folders.Add(handle);
        return handle;
    }

    /// <summary>Opens the file as itself, and checks that it is no link and is where it was asked for; null if there is no file.</summary>
    private SafeFileHandle? OpenFile(uint access, FileShare share, FileMode mode)
    {
        var file = Native.CreateFileW(full, access, share, IntPtr.Zero, mode, OpenReparsePoint, IntPtr.Zero);
        if (file.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            file.Dispose();
            if (error is FileNotFound or PathNotFound)
            {
                return null;
            }

            // A folder, or a link to one, cannot be opened as a file; a folder holds no file to read.
            var attributes = error == AccessDenied && System.IO.Path.Exists(full) ? File.GetAttributes(full) : 0;
            return attributes.HasFlag(FileAttributes.ReparsePoint) ? throw Outside()
                : attributes.HasFlag(FileAttributes.Directory) && mode == FileMode.Open ? null
                : throw Error(error);
        }

        if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) || !string.Equals(FinalPath(file), real, StringComparison.OrdinalIgnoreCase))
        {
            file.Dispose();
            throw Outside();
        }

        return file;
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = Native.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        return length is > 0 and < 32768 ? new string(buffer, 0, (int)length) : throw new IOException("The final path of an open file is unknown.");
    }

    private IOException Error(int? error = null) => new($"{Path}: {Marshal.GetPInvokeErrorMessage(error ?? Marshal.GetLastPInvokeError())}");

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);
    }
}
