using System.Threading.Channels;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// Runs commands isolated from the host and from each other's sandboxes (SBX-01, SBX-06). The built-in sandbox uses each
/// operating system's own isolation (SBX-07); the test kit has a fake one.
/// </summary>
public interface ISandbox
{
    /// <summary>Null when this machine can isolate commands; otherwise what is missing and how to fix it (SBX-07).</summary>
    string? Probe();

    /// <summary>Starts a command. It runs until it exits or the process is disposed, which stops it and everything it started.</summary>
    ValueTask<ISandboxProcess> StartAsync(SandboxCommand command, CancellationToken ct);
}

/// <summary>A command running in a sandbox. Disposing it stops the command and every process it started.</summary>
public interface ISandboxProcess : IAsyncDisposable
{
    /// <summary>
    /// The output as it arrives, one line at a time, standard output and standard error together (SBX-04). It stops at the
    /// output limit with a line that says so, and completes when the command ends.
    /// </summary>
    ChannelReader<string> Output { get; }

    /// <summary>The exit code, once the command and everything it started have ended.</summary>
    Task<int> ExitCode { get; }
}

/// <summary>A command to run in a sandbox.</summary>
/// <param name="CommandLine">The shell command line.</param>
/// <param name="Directory">The agent's working copy: the only host folder the command can see, and where it starts.</param>
/// <param name="Limits">What the command may use.</param>
/// <param name="AllowedHosts">The hosts it may reach through the filtering proxy, such as <c>*.nuget.org</c>; empty means no network.</param>
/// <param name="Environment">Its environment variables, including the secrets its agent may use (SBX-05).</param>
public sealed record SandboxCommand(
    string CommandLine, string Directory, SandboxLimits Limits, IReadOnlyList<string> AllowedHosts, IReadOnlyDictionary<string, string> Environment);

/// <summary>What a command may use (SBX-01). Its time is limited by its owner, which stops it.</summary>
/// <param name="Cpus">The processor time, in cores.</param>
/// <param name="MemoryBytes">The memory of all its processes together.</param>
/// <param name="Processes">How many processes and threads it may have at once.</param>
/// <param name="OutputCharacters">How much output is kept.</param>
public sealed record SandboxLimits(double Cpus, long MemoryBytes, int Processes, int OutputCharacters)
{
    public static SandboxLimits Default { get; } = new(2, 4L << 30, 1024, 10 << 20);
}
