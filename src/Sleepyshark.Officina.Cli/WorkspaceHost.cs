using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Sandbox;
using Sleepyshark.Officina.Workspace;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The git workspace of a run, as the run and its tools use it. A working copy is opened, by its name (<see cref="WorkingCopies"/>),
/// when it is first needed: a task's, which the agents working on and reviewing the task share, or an agent's own for work
/// that is for no task (WS-01). Each agent gets its own <see cref="SandboxTools"/> in a copy. Closing a copy, when its task ends,
/// stops its agents' background processes (SBX-03); disposing the host, when the run ends, closes every copy still open. The
/// command checks run in the sandbox too (WS-02).
/// </summary>
internal sealed class WorkspaceHost : IWorkspace, IAsyncDisposable
{
    private readonly GitWorkspace workspace;
    private readonly OfficinaOptions options;
    private readonly ISandbox? sandbox;
    private readonly bool leaveWorkingCopies;
    private readonly TextWriter warnings;
    private readonly ConcurrentDictionary<string, Lazy<Task<WorkingCopy>>> copies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Copy, string Agent), Lazy<SandboxTools>> sandboxes = new();

    private WorkspaceHost(
        GitWorkspace workspace, OfficinaOptions options, ISandbox? sandbox, string root, bool leaveWorkingCopies, IReadOnlyDictionary<string, ICheck> checks, TextWriter warnings)
    {
        this.warnings = warnings;
        this.workspace = workspace;
        this.leaveWorkingCopies = leaveWorkingCopies;
        this.options = options;
        this.sandbox = sandbox;
        var tools = new Dictionary<string, ITool>(new WorkspaceTools(async call => await OpenAsync(call.WorkingCopy, call.Agent).ConfigureAwait(false)).Tools);
        var gates = new Dictionary<string, IGate>();
        if (sandbox is not null)
        {
            // Only the descriptions are taken from this one; each call runs on its own agent's tools in its working copy.
            foreach (var (id, tool) in new SandboxTools(sandbox, options.Capabilities.Sandbox, options.Capabilities.Workspace, "", root).Tools)
            {
                tools[id] = new SandboxTool(id, tool.Descriptor, this);
            }

            gates[CommandRules.Id] = new CommandRules(options.Capabilities.Sandbox);
        }

        Tools = tools;
        Gates = gates;
        Checks = checks;
    }

    /// <summary>The tools, by the id that <c>extension:&lt;id&gt;</c> sources name; the sandbox's only when its capability is on (CAP-02).</summary>
    public IReadOnlyDictionary<string, ITool> Tools { get; }

    /// <summary>The command rules gate, when the sandbox is on.</summary>
    public IReadOnlyDictionary<string, IGate> Gates { get; }

    /// <summary>The command checks, by the id <see cref="CheckOptions.Id"/> gives them (WS-02, TASK-05).</summary>
    public IReadOnlyDictionary<string, ICheck> Checks { get; }

    /// <summary>The integration queue's length and waiting time (WS-09).</summary>
    public IntegrationQueueStatus Queue => workspace.Queue;

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

        // A command check needs the sandbox, which validation requires of it (SBX-07).
        var checks = sandbox is null
            ? new Dictionary<string, ICheck>()
            : options.Checks.Where(check => check.Value.Command is not null).ToDictionary(
                check => check.Value.Id(check.Key),
                ICheck (check) => new CommandCheck(
                    sandbox, options.Capabilities.Sandbox, options.Capabilities.Workspace, InstructionPlaceholders.FillCommand(check.Value.Command!, options.Project), check.Value.Timeout, time));
        // An application's check is not sof's to run: the runner refuses the configuration for it, as for any unregistered check.
        var baselineChecks = options.Capabilities.Workspace.BaselineChecks.Where(name => checks.ContainsKey(options.Checks[name].Id(name)))
            .ToDictionary(name => name, name => checks[options.Checks[name].Id(name)]);
        var workspace = await GitWorkspace.OpenAsync(
            root, runId, options.Capabilities.Workspace, baselineChecks, time, folder => ReleaseLeftover(sandbox, options, folder, warnings), ct).ConfigureAwait(false);
        try
        {
            await workspace.RemoveLeftoversAsync(keepLeftover, folder => ReleaseLeftover(sandbox, options, folder, warnings), ct).ConfigureAwait(false);
            return new WorkspaceHost(workspace, options, sandbox, root, leaveWorkingCopies, checks, warnings);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>What commands in a folder that is going away left outside it goes too. Failing to remove it must not stop the run.</summary>
    private static void ReleaseLeftover(ISandbox? sandbox, OfficinaOptions options, string folder, TextWriter warnings)
    {
        try
        {
            sandbox?.Release(folder, options.Capabilities.Sandbox.Toolchains);
        }
        catch (AggregateException exception)
        {
            warnings.WriteLine($"warning: could not remove what commands left outside {folder}: {string.Join("; ", exception.InnerExceptions.Select(inner => inner.Message))}");
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

    /// <summary>The working copy of the name, opened once for whoever needs it first; one that failed to open is tried again next time.</summary>
    public async Task<IWorkingCopy> OpenWorkingCopyAsync(string name, string agent, CancellationToken ct) => await OpenAsync(name, agent).ConfigureAwait(false);

    /// <summary>Closes a working copy when its task ends: its agents' background processes stop, and the copy goes unless the owner keeps copies (WS-08).</summary>
    public async Task CloseWorkingCopyAsync(IWorkingCopy copy, CancellationToken ct)
    {
        var closing = (WorkingCopy)copy;
        copies.TryRemove(closing.Name, out _);
        var released = false;
        try
        {
            released = await DisposeSandboxesAsync(closing.Name).ConfigureAwait(false);
        }
        finally
        {
            await workspace.CloseWorkingCopyAsync(closing, ct).ConfigureAwait(false);
            if (!released)
            {
                // Checks may have run commands there even if no agent did.
                ReleaseLeftover(sandbox, options, closing.Directory, warnings);
            }
        }
    }

    public Task<IntegrationResult> IntegrateAsync(IWorkingCopy copy, string task, string author, CancellationToken ct) =>
        workspace.IntegrateAsync((WorkingCopy)copy, task, author, ct);

    public Task<IReadOnlyList<CopySnapshot>> SnapshotAsync(CancellationToken ct) => workspace.SnapshotAsync(ct);

    public Task RestoreAsync(IReadOnlyList<CopySnapshot> snapshot, CancellationToken ct) => workspace.RestoreAsync(snapshot, ct);

    public async ValueTask DisposeAsync()
    {
        // One failing disposal must not leave the others' processes, worktrees or the run lock behind.
        var failures = new List<Exception>();
        if (leaveWorkingCopies)
        {
            workspace.Dispose();
            return;
        }

        var released = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in sandboxes.Keys.Select(key => key.Copy).Distinct().ToList())
        {
            try
            {
                if (await DisposeSandboxesAsync(name).ConfigureAwait(false))
                {
                    released.Add(name);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        // Every copy still open, also those a restore brought back that nobody has used since.
        foreach (var copy in workspace.OpenCopies)
        {
            try
            {
                await workspace.CloseWorkingCopyAsync(copy).ConfigureAwait(false);
                if (!released.Contains(copy.Name))
                {
                    ReleaseLeftover(sandbox, options, copy.Directory, warnings);
                }
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

    private async Task<WorkingCopy> OpenAsync(string name, string agent)
    {
        var opening = copies.GetOrAdd(name, _ => new(() => workspace.OpenWorkingCopyAsync(name, agent)));
        try
        {
            return await opening.Value.ConfigureAwait(false);
        }
        catch
        {
            // A failed open is not kept, so the next call tries again.
            copies.TryRemove(new KeyValuePair<string, Lazy<Task<WorkingCopy>>>(name, opening));
            throw;
        }
    }

    /// <summary>Stops the background processes of every agent in a copy (SBX-03), which releases what the sandbox set up for it.</summary>
    /// <returns>Whether any agent's tools were there, so the copy is released.</returns>
    private async Task<bool> DisposeSandboxesAsync(string name)
    {
        var released = false;
        foreach (var key in sandboxes.Keys.Where(key => key.Copy == name).ToList())
        {
            if (sandboxes.TryRemove(key, out var tools) && tools.IsValueCreated)
            {
                await tools.Value.DisposeAsync().ConfigureAwait(false);
                released = true;
            }
        }

        return released;
    }

    /// <summary>The agent's sandbox tools in the working copy of the call; each agent's commands get its own role's secrets (SBX-05).</summary>
    private async Task<SandboxTools> SandboxOf(ToolCall call)
    {
        var copy = await OpenAsync(call.WorkingCopy, call.Agent).ConfigureAwait(false);
        return sandboxes.GetOrAdd(
            (call.WorkingCopy, call.Agent),
            _ => new(() => new SandboxTools(sandbox!, options.Capabilities.Sandbox, options.Capabilities.Workspace, call.Definition, copy.Directory))).Value;
    }

    private sealed class SandboxTool(string id, ToolDescriptor descriptor, WorkspaceHost host) : ITool
    {
        public ToolDescriptor Descriptor { get; } = descriptor;

        public async ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct) =>
            await (await host.SandboxOf(toolCall).ConfigureAwait(false)).Tools[id].InvokeAsync(toolCall, ct).ConfigureAwait(false);
    }
}
