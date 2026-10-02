using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Sandbox;
using Sleepyshark.Officina.Workspace;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The git workspace of a run, and what the <c>workspace.*</c> and <c>sandbox.*</c> tools need of it. Each agent gets its
/// working copy, and its own <see cref="SandboxTools"/> over it, when it first calls one of them (WS-01). Disposing the
/// host, when the run ends, stops the agents' background processes (SBX-03) and removes their working copies.
/// </summary>
internal sealed partial class WorkspaceHost : IAsyncDisposable
{
    private readonly GitWorkspace workspace;
    private readonly OfficinaOptions options;
    private readonly ISandbox? sandbox;
    private readonly string runId;
    private readonly bool leaveWorkingCopies;
    private readonly ConcurrentDictionary<string, Lazy<Task<Hold>>> holds = new(StringComparer.Ordinal);

    private WorkspaceHost(GitWorkspace workspace, OfficinaOptions options, ISandbox? sandbox, string runId, string root, bool leaveWorkingCopies)
    {
        this.workspace = workspace;
        this.leaveWorkingCopies = leaveWorkingCopies;
        this.options = options;
        this.sandbox = sandbox;
        this.runId = runId;
        var tools = new Dictionary<string, ITool>(new WorkspaceTools(async agent => (await HoldOf(agent).ConfigureAwait(false)).Copy).Tools);
        var gates = new Dictionary<string, IGate>();
        if (sandbox is not null)
        {
            // Only the descriptions are taken from this one; each call runs on its own agent's tools.
            foreach (var (id, tool) in new SandboxTools(sandbox, options.Capabilities.Sandbox, options.Capabilities.Workspace, "", root).Tools)
            {
                tools[id] = new SandboxTool(id, tool.Descriptor, this);
            }

            gates[CommandRules.Id] = new CommandRules(options.Capabilities.Sandbox);
        }

        Tools = tools;
        Gates = gates;
    }

    /// <summary>The tools, by the id that <c>extension:&lt;id&gt;</c> sources name; the sandbox's only when its capability is on (CAP-02).</summary>
    public IReadOnlyDictionary<string, ITool> Tools { get; }

    /// <summary>The command rules gate, when the sandbox is on.</summary>
    public IReadOnlyDictionary<string, IGate> Gates { get; }

    /// <summary>The integration queue's length and waiting time (WS-09).</summary>
    public IntegrationQueueStatus Queue => workspace.Queue;

    /// <summary>The workspace whose working copies checkpoints save and restore (RUN-04).</summary>
    public IWorkspace Workspace => workspace;

    /// <summary>
    /// Opens the workspace in <paramref name="root"/>, after checking that the machine can sandbox commands when they are on, and
    /// removes what a run that died left in it.
    /// </summary>
    /// <param name="options">The configuration.</param>
    /// <param name="root">The project's directory.</param>
    /// <param name="runId">The run that holds the workspace.</param>
    /// <param name="sandbox">What runs commands; null when the sandbox is off.</param>
    /// <param name="time">The clock.</param>
    /// <param name="keepLeftover">Whether a working copy's branch, named by its task, is kept because its run can resume (RUN-04).</param>
    /// <param name="leaveWorkingCopies">Whether the working copies are left in place when the host is disposed, because the run goes on.</param>
    /// <param name="warnings">Where a failure to clean up after a run that died is reported; it does not stop this run.</param>
    /// <param name="ct">Cancels opening.</param>
    /// <exception cref="InvalidOperationException">The sandbox is on and this machine cannot provide it; nothing runs unsandboxed (SBX-07).</exception>
    /// <exception cref="WorkspaceException">The workspace cannot be opened.</exception>
    public static async Task<WorkspaceHost> OpenAsync(
        OfficinaOptions options, string root, string runId, ISandbox? sandbox, TimeProvider time, Func<string, Task<bool>> keepLeftover, bool leaveWorkingCopies,
        TextWriter warnings, CancellationToken ct)
    {
        if (sandbox?.Probe() is { } problem)
        {
            throw new InvalidOperationException($"commands cannot run, because this machine cannot sandbox them: {problem}");
        }

        var workspace = await GitWorkspace.OpenAsync(root, runId, options.Capabilities.Workspace, new Dictionary<string, ICheck>(), time, ct).ConfigureAwait(false);
        try
        {
            await workspace.RemoveLeftoversAsync(keepLeftover, folder => ReleaseLeftover(sandbox, options, folder, warnings), ct).ConfigureAwait(false);
            return new WorkspaceHost(workspace, options, sandbox, runId, root, leaveWorkingCopies);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>What a run that died left outside its working copy goes with the copy. Failing to remove it must not stop the next run.</summary>
    private static void ReleaseLeftover(ISandbox? sandbox, OfficinaOptions options, string folder, TextWriter warnings)
    {
        try
        {
            sandbox?.Release(folder, options.Capabilities.Sandbox.Toolchains);
        }
        catch (AggregateException exception)
        {
            warnings.WriteLine($"warning: could not remove what a run that died left outside {folder}: {string.Join("; ", exception.InnerExceptions.Select(inner => inner.Message))}");
        }
    }

    /// <summary>The sandbox of this machine (SBX-07).</summary>
    public static ISandbox MachineSandbox() => OperatingSystem.IsWindows() ? new WindowsSandbox() : new LinuxSandbox();

    /// <summary>CAP-02: the tools and the gate of a capability are used only when it is on.</summary>
    public static IEnumerable<ConfigurationError> CapabilityErrors(OfficinaOptions options)
    {
        var on = options.Capabilities.Switches();
        var uses = options.Tools.Select(tool => ($"tools.{tool.Key}.source", tool.Value.ExtensionId()))
            .Concat(options.Gates.Select(gate => ($"gates.{gate.Key}.use", gate.Value.ExtensionId())));
        foreach (var (path, id) in uses)
        {
            var capability = id switch
            {
                null => null,
                CommandRules.Id => "sandbox",
                _ when id.StartsWith("sandbox.", StringComparison.Ordinal) => "sandbox",
                _ when id.StartsWith("workspace.", StringComparison.Ordinal) => "workspace",
                _ => null,
            };
            if (capability is not null && !on[capability])
            {
                yield return new(ValidationPhase.Capabilities, path, $"needs the {capability} capability, which is off.", $"Set capabilities.{capability}.enabled to true.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // One failing disposal must not leave the others' processes, worktrees or the run lock behind.
        var failures = new List<Exception>();
        if (leaveWorkingCopies)
        {
            workspace.Dispose();
            return;
        }

        foreach (var hold in holds.Values)
        {
            try
            {
                var (copy, tools) = await hold.Value.ConfigureAwait(false);
                try
                {
                    if (tools is not null)
                    {
                        await tools.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    await workspace.CloseWorkingCopyAsync(copy).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        // Working copies a restore brought back that no agent has used since.
        foreach (var copy in workspace.OpenCopies)
        {
            try
            {
                await workspace.CloseWorkingCopyAsync(copy).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        workspace.Dispose();
        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }

    private async Task<Hold> HoldOf(string agent)
    {
        var hold = holds.GetOrAdd(agent, name => new(() => OpenAsync(name)));
        try
        {
            return await hold.Value.ConfigureAwait(false);
        }
        catch
        {
            // A failed open is not kept, so the agent's next call tries again.
            holds.TryRemove(new KeyValuePair<string, Lazy<Task<Hold>>>(agent, hold));
            throw;
        }
    }

    private async Task<Hold> OpenAsync(string agent)
    {
        var copy = await workspace.OpenWorkingCopyAsync($"{runId}-{SafeName(agent)}", agent).ConfigureAwait(false);
        try
        {
            return new(copy, sandbox is null ? null : new SandboxTools(sandbox, options.Capabilities.Sandbox, options.Capabilities.Workspace, agent, copy.Directory));
        }
        catch
        {
            await workspace.CloseWorkingCopyAsync(copy).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The agent's name as part of a git branch and a folder name; a name with other characters gets a hash, so two such names stay apart.</summary>
    private static string SafeName(string agent) =>
        SafeCharacters().IsMatch(agent)
            ? agent
            : $"{UnsafeCharacters().Replace(agent, "_")}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agent)))[..6]}";

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex SafeCharacters();

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex UnsafeCharacters();

    private sealed record Hold(WorkingCopy Copy, SandboxTools? Tools);

    private sealed class SandboxTool(string id, ToolDescriptor descriptor, WorkspaceHost host) : ITool
    {
        public ToolDescriptor Descriptor { get; } = descriptor;

        public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct) =>
            await (await host.HoldOf(toolCall.Agent).ConfigureAwait(false)).Tools!.Tools[id].InvokeAsync(toolCall, ct).ConfigureAwait(false);
    }
}
