using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Sandbox;

/// <summary>
/// The Windows sandbox (DESIGN.md §7, S00a). Each working copy has its own AppContainer with no capabilities, so its
/// commands have no network and can open only what is granted to the container: the working copy, a home folder of its
/// own, and the toolchains, besides what Windows grants every app (such as Program Files). Protected paths stop inheriting
/// the working copy's grant. Each command runs in a job object that sets its limits and ends everything it started.
/// Allowed traffic goes through the filtering proxy on a named pipe only the container may open, which a small
/// PowerShell forwarder inside the sandbox offers on a loopback port. No admin rights are needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSandbox : ISandbox
{
    // Listens on a free loopback port, says which, and carries each connection to the proxy's pipe. When either direction
    // of a connection ends, both ends are closed, so the client sees the server's close and the proxy the client's. It
    // uses .NET only: cmdlets do not load in the container, and looking one up there takes about 40 seconds.
    private const string Forwarder = """
        $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); [Console]::WriteLine($l.LocalEndpoint.Port)
        $open = [Collections.ArrayList]::new()
        while ($true) {
            if ($l.Pending()) {
                $c = $l.AcceptTcpClient(); $s = $c.GetStream()
                $p = [IO.Pipes.NamedPipeClientStream]::new('.', '{0}', [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous); $p.Connect(5000)
                $null = $open.Add(@($c, $p, $s.CopyToAsync($p), $p.CopyToAsync($s)))
            }
            foreach ($o in @($open)) { if ($o[2].IsCompleted -or $o[3].IsCompleted) { $o[0].Close(); $o[1].Dispose(); $open.Remove($o) } }
            [Threading.Thread]::Sleep(20)
        }
        """;

    // Copied from the host, so programs find Windows and the toolchains.
    private static readonly string[] HostVariables = ["SystemRoot", "windir", "SystemDrive", "ComSpec", "PATH", "PATHEXT", "ProgramFiles",
        "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "ProgramData", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS"];

    public string? Probe()
    {
        // One fixed folder, so every probe reuses the same AppContainer and home folder.
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "officina-probe"));
        try
        {
            var process = StartAsync(new("exit 7", directory.FullName, SandboxLimits.Default, [], new Dictionary<string, string>(), [], [], []), default)
                .AsTask().GetAwaiter().GetResult();
            var exitCode = process.ExitCode.GetAwaiter().GetResult();
            return exitCode == 7 ? null : $"a command in an AppContainer ended with {exitCode} instead of 7.";
        }
        catch (Win32Exception exception)
        {
            return $"commands cannot run in an AppContainer ({exception.Message}). Windows 10 or later is needed.";
        }
    }

    public ValueTask<ISandboxProcess> StartAsync(SandboxCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (name, container) = Container(command.Directory);
        var home = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), name));

        // Modify, not full control, so the container can neither change the entries below nor delete a protected file
        // through its folder.
        Grant(new DirectoryInfo(command.Directory), container, FileSystemRights.Modify);
        Grant(home, container, FileSystemRights.Modify);
        foreach (var toolchain in command.Toolchains.Where(Directory.Exists))
        {
            Grant(new DirectoryInfo(toolchain), container, FileSystemRights.ReadAndExecute);
        }

        foreach (var path in command.HiddenPaths)
        {
            Protect(path, container, null);
        }

        foreach (var path in command.ReadOnlyPaths)
        {
            Protect(path, container, FileSystemRights.ReadAndExecute);
        }

        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in HostVariables)
        {
            if (Environment.GetEnvironmentVariable(variable) is { } value)
            {
                environment[variable] = value;
            }
        }

        environment["PATH"] = string.Join(';', command.Toolchains.Append(environment.GetValueOrDefault("PATH", "")));
        foreach (var variable in new[] { "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP" })
        {
            environment[variable] = home.FullName;
        }

        var job = new Job(command.Limits);
        FilterProxy? proxy = null;

        // The proxy is told before the command can connect, and reports into its output once it runs.
        SandboxProcess? sandboxed = null;
        try
        {
            if (command.AllowedHosts.Count > 0)
            {
                var pipe = $"officina-{Guid.NewGuid():N}";
                proxy = FilterProxy.OnNamedPipe(pipe, container, command.AllowedHosts, line => sandboxed?.Add(line));
                var script = Convert.ToBase64String(Encoding.Unicode.GetBytes(Forwarder.Replace("{0}", pipe, StringComparison.Ordinal)));
                var (_, forwarder) = Launch($"powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand {script}", home.FullName, environment, container, job);
                var line = forwarder.ReadLine();
                if (!int.TryParse(line, out var port))
                {
                    throw new InvalidOperationException($"The proxy forwarder did not start: {line ?? "no output"}");
                }

                // Whatever else it writes is read and dropped, so it never blocks on a full pipe.
                _ = forwarder.ReadToEndAsync(CancellationToken.None);
                environment["HTTP_PROXY"] = environment["HTTPS_PROXY"] = $"http://127.0.0.1:{port}";
            }

            foreach (var (variable, value) in command.Environment)
            {
                environment[variable] = value;
            }

            var (process, output) = Launch($"\"{environment["ComSpec"]}\" /d /s /c \"{command.CommandLine}\"", command.Directory, environment, container, job);
            sandboxed = new SandboxProcess(ExitedAsync(process), [output], job.Dispose, command.Limits.OutputCharacters, proxy);
            return ValueTask.FromResult<ISandboxProcess>(sandboxed);
        }
        catch
        {
            job.Dispose();
            proxy?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>The working copy's AppContainer, created the first time; its name comes from the folder, so each agent has its own (SBX-06).</summary>
    private static (string Name, SecurityIdentifier Sid) Container(string directory)
    {
        var name = $"officina-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory))))[..16]}";
        var result = Native.CreateAppContainerProfile(name, name, "Officina sandbox", IntPtr.Zero, 0, out var sid);
        if (result == AlreadyExists)
        {
            result = Native.DeriveAppContainerSidFromAppContainerName(name, out sid);
        }

        if (result != 0)
        {
            throw new Win32Exception(result);
        }

        try
        {
            return (name, new SecurityIdentifier(sid));
        }
        finally
        {
            Native.FreeSid(sid);
        }
    }

    // HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS).
    private const int AlreadyExists = unchecked((int)0x800700B7);

    // Setting an entry rewrites the entries of everything below, so each is set only once.
    private const InheritanceFlags Everything = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>Grants the container rights over a folder and everything in it.</summary>
    private static void Grant(DirectoryInfo folder, SecurityIdentifier container, FileSystemRights rights)
    {
        var security = folder.GetAccessControl();
        if (!security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule => rule.IdentityReference == container))
        {
            security.AddAccessRule(new FileSystemAccessRule(container, rights, Everything, PropagationFlags.None, AccessControlType.Allow));
            folder.SetAccessControl(security);
        }
    }

    /// <summary>
    /// Takes a protected path out of the working copy's grant: it stops inheriting, and the container keeps only the
    /// rights given, if any. An AppContainer opens only what is granted to it, so a hidden path cannot be opened at all.
    /// An entry that denies the container does not stop it, as tests on Windows showed.
    /// </summary>
    private static void Protect(string path, SecurityIdentifier container, FileSystemRights? rights)
    {
        FileSystemInfo item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var security = Read(item);
        var held = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference == container).ToList();
        if (security.AreAccessRulesProtected && held.All(rule => (rule.FileSystemRights & ~FileSystemRights.Synchronize) == rights))
        {
            return;
        }

        // Inherited entries become the item's own only once written, so the container's is removed in a second write.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        Write(item, security);
        security = Read(item);
        security.PurgeAccessRules(container);
        if (rights is { } kept)
        {
            security.AddAccessRule(item is DirectoryInfo
                ? new FileSystemAccessRule(container, kept, Everything, PropagationFlags.None, AccessControlType.Allow)
                : new FileSystemAccessRule(container, kept, AccessControlType.Allow));
        }

        Write(item, security);
    }

    private static FileSystemSecurity Read(FileSystemInfo item) =>
        item is DirectoryInfo folder ? folder.GetAccessControl() : ((FileInfo)item).GetAccessControl();

    private static void Write(FileSystemInfo item, FileSystemSecurity security)
    {
        if (item is DirectoryInfo folder)
        {
            folder.SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            ((FileInfo)item).SetAccessControl((FileSecurity)security);
        }
    }

    private static async Task<int> ExitedAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
    }

    /// <summary>Starts a process in the container and the job, with its output and errors on one pipe and no input.</summary>
    private static (Process Process, StreamReader Output) Launch(
        string commandLine, string directory, SortedDictionary<string, string> environment, SecurityIdentifier container, Job job)
    {
        using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var sid = new byte[container.BinaryLength];
        container.GetBinaryForm(sid, 0);
        var sidMemory = Marshal.AllocHGlobal(sid.Length);
        var capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SecurityCapabilities>());
        var handles = Marshal.AllocHGlobal(2 * IntPtr.Size);
        var block = Marshal.StringToHGlobalUni(string.Concat(environment.Select(variable => $"{variable.Key}={variable.Value}\0")) + "\0");
        var attributes = IntPtr.Zero;
        try
        {
            Marshal.Copy(sid, 0, sidMemory, sid.Length);
            Marshal.StructureToPtr(new Native.SecurityCapabilities { AppContainerSid = sidMemory }, capabilities, false);
            Marshal.Copy(new[] { input.ClientSafePipeHandle.DangerousGetHandle(), output.ClientSafePipeHandle.DangerousGetHandle() }, 0, handles, 2);

            // The process runs in the container with no capabilities, and inherits only its two pipe handles.
            var size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            Check(Native.InitializeProcThreadAttributeList(attributes, 2, 0, ref size));
            Check(Native.UpdateProcThreadAttribute(attributes, 0, Native.SecurityCapabilitiesAttribute, capabilities,
                Marshal.SizeOf<Native.SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero));
            Check(Native.UpdateProcThreadAttribute(attributes, 0, Native.HandleListAttribute, handles, 2 * IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            var startup = new Native.StartupInfoEx
            {
                StartupInfo = new()
                {
                    Size = Marshal.SizeOf<Native.StartupInfoEx>(),
                    Flags = Native.UseStdHandles,
                    StdInput = input.ClientSafePipeHandle.DangerousGetHandle(),
                    StdOutput = output.ClientSafePipeHandle.DangerousGetHandle(),
                    StdError = output.ClientSafePipeHandle.DangerousGetHandle(),
                },
                AttributeList = attributes,
            };

            // Suspended until it is in the job, so nothing it starts escapes the limits.
            Check(Native.CreateProcessW(null, (commandLine + '\0').ToCharArray(), IntPtr.Zero, IntPtr.Zero, true, Native.CreationFlags, block, directory,
                ref startup, out var started));
            try
            {
                Check(Native.AssignProcessToJobObject(job.Handle, started.Process));
                var process = Process.GetProcessById(started.ProcessId);
                _ = process.SafeHandle;
                Check(Native.ResumeThread(started.Thread) != uint.MaxValue);
                output.DisposeLocalCopyOfClientHandle();
                return (process, new StreamReader(output));
            }
            finally
            {
                Native.CloseHandle(started.Thread);
                Native.CloseHandle(started.Process);
            }
        }
        catch
        {
            output.Dispose();
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            Marshal.FreeHGlobal(block);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(capabilities);
            Marshal.FreeHGlobal(sidMemory);
        }
    }

    private static void Check(bool succeeded)
    {
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>A command's job object: its limits, and the end of everything in it when it is closed.</summary>
    private sealed class Job : IDisposable
    {
        private IntPtr handle;

        public Job(SandboxLimits limits)
        {
            handle = Native.CreateJobObjectW(IntPtr.Zero, null);
            Check(handle != IntPtr.Zero);
            var extended = new Native.ExtendedLimits
            {
                Basic = new()
                {
                    LimitFlags = Native.KillOnJobClose | Native.LimitActiveProcesses | Native.LimitJobMemory | Native.DieOnUnhandledException,
                    ActiveProcessLimit = (uint)limits.Processes,
                },
                JobMemoryLimit = checked((UIntPtr)(ulong)limits.MemoryBytes),
            };
            Check(Native.SetInformationJobObject(handle, Native.ExtendedLimitInformation, ref extended, Marshal.SizeOf<Native.ExtendedLimits>()));

            // A hard cap, in hundredths of a percent of all the machine's processors.
            var cpu = new Native.CpuRate { ControlFlags = 0x1 | 0x4, Rate = (uint)Math.Clamp(limits.Cpus / Environment.ProcessorCount * 10_000, 1, 10_000) };
            Check(Native.SetInformationJobObject(handle, Native.CpuRateControlInformation, ref cpu, Marshal.SizeOf<Native.CpuRate>()));
        }

        public IntPtr Handle => handle;

        /// <summary>Ends every process in the job. Safe to call more than once.</summary>
        public void Dispose()
        {
            var closing = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (closing != IntPtr.Zero)
            {
                Native.TerminateJobObject(closing, 1);
                Native.CloseHandle(closing);
            }
        }
    }

    private static class Native
    {
        public const int UseStdHandles = 0x100;
        public const uint CreationFlags = 0x00080000 | 0x4 | 0x400 | 0x08000000; // extended startup info, suspended, Unicode environment, no window
        public const int ExtendedLimitInformation = 9;
        public const int CpuRateControlInformation = 15;
        public const uint LimitActiveProcesses = 0x8;
        public const uint LimitJobMemory = 0x200;
        public const uint DieOnUnhandledException = 0x400;
        public const uint KillOnJobClose = 0x2000;
        public static readonly IntPtr HandleListAttribute = 0x00020002;
        public static readonly IntPtr SecurityCapabilitiesAttribute = 0x00020009;

        [StructLayout(LayoutKind.Sequential)]
        public struct SecurityCapabilities
        {
            public IntPtr AppContainerSid;
            public IntPtr Capabilities;
            public uint CapabilityCount;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved;
            public IntPtr Desktop;
            public IntPtr Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2Size;
            public IntPtr Reserved2;
            public IntPtr StdInput;
            public IntPtr StdOutput;
            public IntPtr StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BasicLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CpuRate
        {
            public uint ControlFlags;
            public uint Rate;
        }

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        public static extern int CreateAppContainerProfile(string name, string displayName, string description, IntPtr capabilities, uint count, out IntPtr sid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        public static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);

        [DllImport("advapi32.dll")]
        public static extern IntPtr FreeSid(IntPtr sid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimits information, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref CpuRate information, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(
            string? application, char[] commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags,
            IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);

        [DllImport("kernel32.dll")]
        public static extern uint ResumeThread(IntPtr thread);
    }
}
