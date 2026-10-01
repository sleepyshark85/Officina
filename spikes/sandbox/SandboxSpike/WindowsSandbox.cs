using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SandboxSpike;

/// <summary>
/// AppContainer (no capabilities = no network) + Job Object (CPU rate hard cap, job memory,
/// active process count, kill-on-close). The working copy is the only path ACL'd to the
/// AppContainer SID.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsSandbox : ISandbox
{
    public string WorkDir { get; }
    public string ContainerName { get; }
    public SecurityIdentifier Sid { get; }

    public WindowsSandbox(string workDir, string containerName)
    {
        WorkDir = workDir; ContainerName = containerName;
        int hr = CreateAppContainerProfile(containerName, containerName, "Officina sandbox spike", IntPtr.Zero, 0, out var psid);
        if (hr == unchecked((int)0x800700B7)) // already exists
            hr = DeriveAppContainerSidFromAppContainerName(containerName, out psid);
        if (hr != 0) throw new Win32Exception(hr, "AppContainer profile");
        Sid = new SecurityIdentifier(psid);
        FreeSid(psid);

        Directory.CreateDirectory(workDir);
        var di = new DirectoryInfo(workDir);
        var sec = di.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(Sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
    }

    public static void DeleteProfile(string name) => DeleteAppContainerProfile(name);

    public Task<RunResult> RunShellAsync(string commandLine, Limits limits, CancellationToken ct = default,
        IDictionary<string, string>? extraEnv = null) =>
        Task.Factory.StartNew(() => RunCore(commandLine, limits, ct, extraEnv), TaskCreationOptions.LongRunning);

    private RunResult RunCore(string commandLine, Limits limits, CancellationToken ct, IDictionary<string, string>? extraEnv)
    {
        var facts = new Dictionary<string, string>();
        var home = Path.Combine(WorkDir, ".home"); var tmp = Path.Combine(WorkDir, ".tmp");
        Directory.CreateDirectory(home); Directory.CreateDirectory(tmp);
        var dotnetDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        var sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = sysRoot, ["windir"] = sysRoot, ["SystemDrive"] = Path.GetPathRoot(sysRoot)!.TrimEnd('\\'),
            ["ComSpec"] = Path.Combine(sysRoot, "System32", "cmd.exe"),
            ["PATH"] = $"{dotnetDir};{sysRoot}\\System32;{sysRoot}",
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["ProgramFiles"] = Environment.GetEnvironmentVariable("ProgramFiles") ?? "",
            ["ProgramFiles(x86)"] = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "",
            ["ProgramData"] = Environment.GetEnvironmentVariable("ProgramData") ?? "",
            ["NUMBER_OF_PROCESSORS"] = Environment.ProcessorCount.ToString(),
            ["PROCESSOR_ARCHITECTURE"] = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "AMD64",
            ["USERPROFILE"] = home, ["HOME"] = home, ["APPDATA"] = Path.Combine(home, "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local"), ["TEMP"] = tmp, ["TMP"] = tmp,
            ["DOTNET_CLI_HOME"] = home, ["NUGET_PACKAGES"] = Path.Combine(WorkDir, ".nuget", "packages"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1", ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "1", ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1", ["UseSharedCompilation"] = "false",
        };
        Directory.CreateDirectory(env["APPDATA"]); Directory.CreateDirectory(env["LOCALAPPDATA"]);
        if (extraEnv is not null) foreach (var (k, v) in extraEnv) env[k] = v;
        var envBlock = new StringBuilder();
        foreach (var (k, v) in env) envBlock.Append(k).Append('=').Append(v).Append('\0');
        envBlock.Append('\0');

        // Job object with limits.
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception();
        var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        ext.Basic.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS |
                               JOB_OBJECT_LIMIT_JOB_MEMORY | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        ext.Basic.ActiveProcessLimit = (uint)limits.MaxProcesses;
        ext.JobMemoryLimit = (UIntPtr)(ulong)limits.MemoryBytes;
        if (!SetInformationJobObject(job, 9, &ext, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))) throw new Win32Exception();
        var cpu = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = 0x1 | 0x4, // ENABLE | HARD_CAP
            CpuRate = (uint)Math.Clamp(limits.CpuCores / Environment.ProcessorCount * 10000, 1, 10000),
        };
        if (!SetInformationJobObject(job, 15, &cpu, (uint)sizeof(JOBOBJECT_CPU_RATE_CONTROL_INFORMATION)))
            facts["cpu_rate_error"] = Marshal.GetLastWin32Error().ToString();

        // Pipes: stdout+stderr -> one pipe; stdin -> empty pipe.
        var sa = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), bInheritHandle = 1 };
        if (!CreatePipe(out var outRead, out var outWrite, ref sa, 0)) throw new Win32Exception();
        SetHandleInformation(outRead, 1, 0);
        if (!CreatePipe(out var inRead, out var inWrite, ref sa, 0)) throw new Win32Exception();
        SetHandleInformation(inWrite, 1, 0);
        CloseHandle(inWrite);

        // Attribute list: SECURITY_CAPABILITIES (AppContainer, zero capabilities) + HANDLE_LIST.
        var sidBytes = new byte[Sid.BinaryLength]; Sid.GetBinaryForm(sidBytes, 0);
        var sidPtr = Marshal.AllocHGlobal(sidBytes.Length); Marshal.Copy(sidBytes, 0, sidPtr, sidBytes.Length);
        var caps = new SECURITY_CAPABILITIES { AppContainerSid = sidPtr };
        var handles = stackalloc IntPtr[2]; handles[0] = outWrite; handles[1] = inRead;
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
        var attr = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attr, 2, 0, ref size)) throw new Win32Exception();
        if (!UpdateProcThreadAttribute(attr, 0, (IntPtr)0x00020009, &caps, (IntPtr)sizeof(SECURITY_CAPABILITIES), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
        if (!UpdateProcThreadAttribute(attr, 0, (IntPtr)0x00020002, handles, (IntPtr)(2 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();

        var si = new STARTUPINFOEXW();
        si.StartupInfo.cb = sizeof(STARTUPINFOEXW);
        si.StartupInfo.dwFlags = 0x100; // STARTF_USESTDHANDLES
        si.StartupInfo.hStdInput = inRead; si.StartupInfo.hStdOutput = outWrite; si.StartupInfo.hStdError = outWrite;
        si.lpAttributeList = attr;
        var cmd = new StringBuilder($"\"{env["ComSpec"]}\" /d /s /c \"{commandLine}\"");
        var envPtr = Marshal.StringToHGlobalUni(envBlock.ToString());
        const uint flags = 0x00080000 | 0x4 | 0x400 | 0x08000000; // EXTENDED_STARTUPINFO | SUSPENDED | UNICODE_ENV | NO_WINDOW
        bool ok = CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, true, flags, envPtr, WorkDir, ref si, out var pi);
        int err = Marshal.GetLastWin32Error();
        CloseHandle(outWrite); CloseHandle(inRead);
        DeleteProcThreadAttributeList(attr); Marshal.FreeHGlobal(attr); Marshal.FreeHGlobal(envPtr); Marshal.FreeHGlobal(sidPtr);
        if (!ok) { CloseHandle(outRead); CloseHandle(job); throw new Win32Exception(err, "CreateProcess in AppContainer"); }
        if (!AssignProcessToJobObject(job, pi.hProcess)) { var e = Marshal.GetLastWin32Error(); TerminateProcess(pi.hProcess, 1); throw new Win32Exception(e, "AssignProcessToJobObject"); }
        ResumeThread(pi.hThread); CloseHandle(pi.hThread);

        var sink = new OutputSink();
        var reader = Task.Factory.StartNew(() =>
        {
            using var fs = new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read, 4096, false);
            using var sr = new StreamReader(fs);
            string? l; while ((l = sr.ReadLine()) != null) sink.Add(l);
        }, TaskCreationOptions.LongRunning);

        var sw = Stopwatch.StartNew();
        bool timedOut = false, cancelled = false;
        uint peakActive = 0;
        while (WaitForSingleObject(pi.hProcess, 100) != 0)
        {
            var acc = Accounting(job); peakActive = Math.Max(peakActive, acc.ActiveProcesses);
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            if (sw.Elapsed > limits.WallTime) { timedOut = true; break; }
        }
        GetExitCodeProcess(pi.hProcess, out var exit);
        var before = Accounting(job);
        facts["active_processes_before_kill"] = before.ActiveProcesses.ToString();
        facts["total_processes"] = before.TotalProcesses.ToString();
        facts["peak_active_processes"] = Math.Max(peakActive, before.ActiveProcesses).ToString();
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION q;
        if (QueryInformationJobObject(job, 9, &q, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION), IntPtr.Zero))
            facts["peak_job_memory_mb"] = ((ulong)q.PeakJobMemoryUsed >> 20).ToString();
        // Kill everything that is still in the job (background, grandchildren).
        TerminateJobObject(job, 0xC0000409);
        for (int i = 0; i < 50 && Accounting(job).ActiveProcesses > 0; i++) Thread.Sleep(100);
        facts["active_processes_after_kill"] = Accounting(job).ActiveProcesses.ToString();
        if (timedOut || cancelled) GetExitCodeProcess(pi.hProcess, out exit);
        CloseHandle(pi.hProcess); CloseHandle(job);
        reader.Wait(TimeSpan.FromSeconds(5));
        facts["wall_ms"] = sw.ElapsedMilliseconds.ToString();
        return new RunResult(unchecked((int)exit), sink.ToString(), timedOut, cancelled, facts);
    }

    private static JOBOBJECT_BASIC_ACCOUNTING_INFORMATION Accounting(IntPtr job)
    {
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION a;
        QueryInformationJobObject(job, 1, &a, (uint)sizeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION), IntPtr.Zero);
        return a;
    }

    // ---- interop ----
    const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x8, JOB_OBJECT_LIMIT_JOB_MEMORY = 0x200,
        JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x400, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
    [StructLayout(LayoutKind.Sequential)] struct SECURITY_CAPABILITIES { public IntPtr AppContainerSid; public IntPtr Capabilities; public uint CapabilityCount; public uint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOW
    {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEXW { public STARTUPINFOW StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic; public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)] struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION { public uint ControlFlags, CpuRate; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] static extern int CreateAppContainerProfile(string name, string display, string desc, IntPtr caps, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObjectW(IntPtr sa, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, void* info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(IntPtr job, int cls, void* info, uint len, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr p, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out IntPtr r, out IntPtr w, ref SECURITY_ATTRIBUTES sa, int size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, void* value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFOEXW si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr t);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
}
