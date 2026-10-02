using System.ComponentModel;
using System.Diagnostics;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Sandbox;

/// <summary>
/// The Linux sandbox (DESIGN.md §7, S00a). Each command runs in bubblewrap with every namespace unshared: only the
/// toolchains, read-only, and the working copy exist, and the network has only loopback. Allowed traffic goes through
/// the filtering proxy, whose Unix socket socat forwards to <c>127.0.0.1:3128</c> inside. A transient systemd user
/// scope sets the cgroup limits. Killing bubblewrap ends its process namespace, and with it everything the command
/// started.
/// </summary>
public sealed class LinuxSandbox : ISandbox
{
    private const string ProxySocket = "/run/officina/proxy.sock";
    private const string ProxyAddress = "http://127.0.0.1:3128";

    // Toolchains are read from here; nothing else of the host is visible.
    private static readonly string[] ReadOnly = ["/usr", "/bin", "/lib", "/lib64", "/sbin", "/etc/ssl", "/etc/ca-certificates", "/etc/alternatives",
        "/etc/nsswitch.conf", "/etc/localtime"];

    /// <summary>Commands here leave nothing outside the working copy: everything they use is a mount that ends with the command.</summary>
    public void Release(string directory, IReadOnlyList<string> toolchains)
    {
    }

    public string? Probe()
    {
        if (Fails("bwrap", "--unshare-all", "--die-with-parent", "--ro-bind", "/", "/", "true") is { } bwrap)
        {
            return $"bubblewrap cannot create namespaces ({bwrap}). Install bubblewrap; on Ubuntu 23.10 and later, also install an AppArmor profile that lets /usr/bin/bwrap create user namespaces.";
        }

        if (Fails("systemd-run", "--user", "--scope", "--quiet", "true") is { } systemd)
        {
            return $"systemd cannot limit commands ({systemd}). The account needs a systemd user manager: as root, run loginctl enable-linger <user> once.";
        }

        return Fails("socat", "-V") is { } socat ? $"socat is missing ({socat}). Install it; it carries allowed traffic to the proxy." : null;
    }

    public ValueTask<ISandboxProcess> StartAsync(SandboxCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var limits = command.Limits;
        var start = new ProcessStartInfo("systemd-run");

        // A process killed for memory ends alone; the command decides what happens next, as it would outside.
        Add(start, "--user", "--scope", "--quiet", "--collect", "-p", $"MemoryMax={limits.MemoryBytes}", "-p", "MemorySwapMax=0", "-p", "OOMPolicy=continue",
            "-p", $"CPUQuota={(int)(limits.Cpus * 100)}%", "-p", $"TasksMax={limits.Processes}",
            "--", "bwrap", "--unshare-all", "--die-with-parent", "--new-session", "--clearenv");
        foreach (var path in ReadOnly)
        {
            // On merged-/usr systems /bin and the others are links into /usr.
            Add(start, new FileInfo(path).LinkTarget is { } target ? ["--symlink", target, path] : ["--ro-bind-try", path, path]);
        }

        Add(start, "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--bind", command.Directory, command.Directory, "--chdir", command.Directory);

        // Protected paths (WS-05): a hidden folder becomes empty, a hidden file reads as empty, a read-only path is mounted read-only.
        foreach (var path in command.HiddenPaths)
        {
            Add(start, Directory.Exists(path) ? ["--tmpfs", path] : ["--ro-bind", "/dev/null", path]);
        }

        foreach (var path in command.ReadOnlyPaths.Concat(command.Toolchains))
        {
            Add(start, "--ro-bind-try", path, path);
        }

        var environment = new Dictionary<string, string>
        {
            ["PATH"] = string.Join(':', command.Toolchains.Append("/usr/local/bin:/usr/bin:/bin:/usr/local/sbin:/usr/sbin:/sbin")),
            ["HOME"] = "/tmp",
            ["TMPDIR"] = "/tmp",
            ["LANG"] = "C.UTF-8",
        };
        var commandLine = command.CommandLine;
        FilterProxy? proxy = null;

        // The proxy is told before the command can connect, and reports into its output once it runs.
        SandboxProcess? sandboxed = null;
        if (command.AllowedHosts.Count > 0)
        {
            // A Unix socket path is limited to 108 bytes, so it lives in the runtime folder, not the working copy.
            var socket = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(), $"officina-{Guid.NewGuid():N}.sock");
            proxy = FilterProxy.OnUnixSocket(socket, command.AllowedHosts, line => sandboxed?.Add(line));
            Add(start, "--bind", socket, ProxySocket);
            foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
            {
                environment[name] = ProxyAddress;
            }

            // Port 3128 is 0C38 in /proc/net/tcp; the command starts once socat listens there.
            commandLine = $"socat TCP-LISTEN:3128,bind=127.0.0.1,fork,reuseaddr UNIX-CONNECT:{ProxySocket} & "
                + "i=0; while [ $i -lt 100 ] && ! grep -q ':0C38 ' /proc/net/tcp; do sleep 0.05; i=$((i+1)); done; " + commandLine;
        }

        foreach (var (name, value) in environment.Concat(command.Environment))
        {
            Add(start, "--setenv", name, value);
        }

        Add(start, "--", "/bin/sh", "-c", commandLine);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var process = Process.Start(start)!;
        process.StandardInput.Close();
        sandboxed = new SandboxProcess(
            ExitedAsync(process), [process.StandardOutput, process.StandardError], () => process.Kill(entireProcessTree: true), limits.OutputCharacters, proxy);
        return ValueTask.FromResult<ISandboxProcess>(sandboxed);
    }

    private static async Task<int> ExitedAsync(Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void Add(ProcessStartInfo start, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
    }

    /// <summary>Runs a program to see whether it works; null if it does, otherwise what went wrong.</summary>
    private static string? Fails(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        Add(start, arguments);
        try
        {
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? null : error.Result.Trim();
        }
        catch (Win32Exception exception)
        {
            return exception.Message;
        }
    }
}
