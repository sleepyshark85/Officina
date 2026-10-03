using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Workspace;

/// <summary>
/// A run's hold on a git workspace (DESIGN.md §7). The baseline is the branch checked out at the root. Each agent
/// works in its own worktree on <c>agent/&lt;task&gt;</c>, and changes reach the baseline only through the integration
/// queue. One run holds a workspace at a time (RUN-12); disposing it lets the next run in.
/// </summary>
public sealed partial class GitWorkspace : IWorkspace, IDisposable
{
    private readonly string root;
    private readonly WorkspaceOptions options;
    private readonly FileStream runLock;
    private readonly IntegrationQueue queue;
    private readonly Matcher hidden;
    private readonly Matcher readOnly;
    private readonly ConcurrentDictionary<string, WorkingCopy> open = new(StringComparer.Ordinal);

    private GitWorkspace(
        string root, string baseline, string runId, WorkspaceOptions options, IReadOnlyDictionary<string, ICheck> baselineChecks, TimeProvider time, FileStream runLock, Action<string>? released)
    {
        this.root = root;
        this.options = options;
        this.runLock = runLock;
        var paths = WorkspaceOptions.FixedProtectedPaths.Concat(options.ProtectedPaths).ToList();
        hidden = Globs(paths.Where(path => path.Access == PathAccess.Hidden));
        readOnly = Globs(paths.Where(path => path.Access == PathAccess.ReadOnly));
        queue = new IntegrationQueue(root, baseline, runId, baselineChecks, time, released, Globs(paths));
    }

    /// <summary>The working copies that are open now.</summary>
    public IReadOnlyCollection<WorkingCopy> OpenCopies => [.. open.Values];

    private string WorktreeFolder => Path.Combine(root, WorkspaceOptions.StateFolder, "worktrees");

    /// <summary>The integration queue's length and waiting time (WS-09).</summary>
    public IntegrationQueueStatus Queue => queue.Status;

    /// <summary>Takes the workspace for a run. A second run is refused while the first holds it (RUN-12).</summary>
    /// <param name="root">The top folder of a git repository, with the baseline branch checked out.</param>
    /// <param name="runId">The run, named to any other run that tries to open the workspace.</param>
    /// <param name="options">The workspace settings.</param>
    /// <param name="baselineChecks">The checks a change must pass on the baseline before it is integrated (WS-02), by name.</param>
    /// <param name="time">The clock for the queue's waiting time.</param>
    /// <param name="released">Called with the folder the baseline checks run in before they run and again before it is removed; null for nothing. If it throws before the checks, the integration fails.</param>
    /// <param name="ct">Cancels opening.</param>
    /// <exception cref="WorkspaceException">Another run holds the workspace, or the root has no branch checked out.</exception>
    public static async Task<GitWorkspace> OpenAsync(
        string root, string runId, WorkspaceOptions options, IReadOnlyDictionary<string, ICheck> baselineChecks, TimeProvider time, Action<string>? released = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(baselineChecks);
        ArgumentNullException.ThrowIfNull(time);
        root = Path.GetFullPath(root);
        var state = Directory.CreateDirectory(Path.Combine(root, WorkspaceOptions.StateFolder)).FullName;
        var activeRun = Path.Combine(state, "run");
        FileStream runLock;
        try
        {
            // The operating system releases the lock if the process dies, so a crashed run never blocks the next one.
            runLock = new FileStream(Path.Combine(state, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            string active;
            try
            {
                active = $"Run {File.ReadAllText(activeRun)}";
            }
            catch (IOException)
            {
                // The other run holds the lock but has not written its name yet.
                active = "Another run";
            }

            throw new WorkspaceException($"{active} is already active on this workspace. Start this run when it ends.");
        }

        try
        {
            await File.WriteAllTextAsync(activeRun, runId, ct).ConfigureAwait(false);
            var (exitCode, branch, _) = await Git.TryRunAsync(root, ct, "symbolic-ref", "--short", "HEAD").ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new WorkspaceException($"{root} must be a git repository with the baseline branch checked out.");
            }

            // Working copies, diffs and protected paths are all relative to the repository's top folder, so the workspace is that
            // folder: in a subfolder, the configuration there would be neither where the protected paths say nor protected.
            var prefix = (await Git.RunAsync(root, ct, "rev-parse", "--show-prefix").ConfigureAwait(false)).Trim();
            return prefix.Length == 0
                ? new GitWorkspace(root, branch.Trim(), runId, options, baselineChecks, time, runLock, released)
                : throw new WorkspaceException($"{root} is the subfolder {prefix} of a git repository. Put sof.json in the repository's top folder.");
        }
        catch
        {
            await runLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates the working copy where an agent does a task, from the baseline as it is now (WS-01). One already open for the
    /// task, or brought back by <see cref="RestoreAsync"/>, is returned as it is.
    /// </summary>
    public async Task<WorkingCopy> OpenWorkingCopyAsync(string name, string agent, CancellationToken ct = default)
    {
        if (open.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var copy = new WorkingCopy(name, agent, Path.Combine(WorktreeFolder, name), hidden, readOnly);
        await Git.RunAsync(root, ct, "worktree", "add", "-b", copy.Branch, copy.Directory, "HEAD").ConfigureAwait(false);
        open[name] = copy;
        return copy;
    }

    /// <summary>
    /// Removes what a run that died left behind: its working copies' folders, and the branches of runs that cannot resume.
    /// Only working copies named as the host names them, <c>&lt;run id&gt;-&lt;agent&gt;</c> with a GUID run id, are touched: other
    /// <c>agent/*</c> branches are the owner's. Nothing is removed when the owner keeps working copies (WS-08). Call it once the
    /// run holds the workspace, so no other run is using any of it.
    /// </summary>
    /// <param name="keep">Whether a task's branch, with the commits of its checkpoints, is kept because its run can resume (RUN-04).</param>
    /// <param name="removing">Called with each working copy's folder before it is removed, such as to release what a sandbox left outside it; null for nothing.</param>
    /// <param name="ct">Cancels the cleanup.</param>
    public async Task RemoveLeftoversAsync(Func<string, Task<bool>> keep, Action<string>? removing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keep);
        if (options.KeepWorkingCopies)
        {
            return;
        }

        await Git.RunAsync(root, ct, "worktree", "prune").ConfigureAwait(false);
        if (Directory.Exists(WorktreeFolder))
        {
            foreach (var folder in Directory.EnumerateDirectories(WorktreeFolder).Where(folder => RunsCopy().IsMatch(Path.GetFileName(folder)) && !open.ContainsKey(Path.GetFileName(folder))))
            {
                removing?.Invoke(folder);
                await Git.TryRunAsync(root, ct, "worktree", "remove", "--force", folder).ConfigureAwait(false);
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }

        await Git.RunAsync(root, ct, "worktree", "prune").ConfigureAwait(false);
        const string prefix = "agent/";
        foreach (var branch in (await Git.RunAsync(root, ct, "for-each-ref", "--format=%(refname:short)", $"refs/heads/{prefix}").ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var task = branch.Trim()[prefix.Length..];
            if (RunsCopy().IsMatch(task) && !open.ContainsKey(task) && !await keep(task).ConfigureAwait(false))
            {
                await Git.RunAsync(root, ct, "branch", "-D", branch.Trim()).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Saves every open working copy: changes not yet committed are committed first, as a checkpoint commit on its branch,
    /// and the commit it is at is the snapshot (DESIGN.md §8).
    /// </summary>
    public async Task<IReadOnlyList<CopySnapshot>> SnapshotAsync(CancellationToken ct = default)
    {
        var snapshots = new List<CopySnapshot>();
        foreach (var copy in open.Values.OrderBy(copy => copy.Name, StringComparer.Ordinal))
        {
            if ((await Git.RunAsync(copy.Directory, ct, "status", "--porcelain").ConfigureAwait(false)).Length > 0)
            {
                await Git.RunAsync(copy.Directory, ct, "add", "--all").ConfigureAwait(false);
                await Git.RunAsync(copy.Directory, ct, "-c", "user.name=Officina", "-c", "user.email=officina@localhost", "-c", "commit.gpgsign=false", "commit", "--no-verify", "--message", "Officina checkpoint").ConfigureAwait(false);
            }

            snapshots.Add(new CopySnapshot(copy.Name, copy.Agent, (await Git.RunAsync(copy.Directory, ct, "rev-parse", "HEAD").ConfigureAwait(false)).Trim()));
        }

        return snapshots;
    }

    /// <summary>
    /// Returns the working copies to a snapshot: each is reset to its commit and cleaned of files added since, from its
    /// branch, or from the commit alone if the branch is gone. A working copy that is not in the snapshot is removed.
    /// </summary>
    public async Task RestoreAsync(IReadOnlyList<CopySnapshot> snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var saved = snapshot.Select(copy => copy.TaskId).ToHashSet(StringComparer.Ordinal);
        foreach (var added in open.Values.Where(copy => !saved.Contains(copy.Name)).ToList())
        {
            await CloseWorkingCopyAsync(added, ct).ConfigureAwait(false);
        }

        foreach (var saving in snapshot)
        {
            if (!open.TryGetValue(saving.TaskId, out var copy))
            {
                copy = new WorkingCopy(saving.TaskId, saving.Agent, Path.Combine(WorktreeFolder, saving.TaskId), hidden, readOnly);
                if (!Directory.Exists(copy.Directory))
                {
                    await Git.RunAsync(root, ct, "worktree", "prune").ConfigureAwait(false);
                    var (exists, _, _) = await Git.TryRunAsync(root, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{copy.Branch}").ConfigureAwait(false);
                    await (exists == 0
                        ? Git.RunAsync(root, ct, "worktree", "add", copy.Directory, copy.Branch)
                        : Git.RunAsync(root, ct, "worktree", "add", "-b", copy.Branch, copy.Directory, saving.Commit)).ConfigureAwait(false);
                }

                open[saving.TaskId] = copy;
            }

            await Git.RunAsync(copy.Directory, ct, "reset", "--hard", saving.Commit).ConfigureAwait(false);
            await Git.RunAsync(copy.Directory, ct, "clean", "-fd").ConfigureAwait(false);
        }
    }

    /// <summary>Queues a task's change for integration, attributed to the task and its author (WS-04), and waits for the result (WS-09).</summary>
    public Task<IntegrationResult> IntegrateAsync(WorkingCopy copy, string task, string author, CancellationToken ct = default) =>
        IntegrateAsync(copy, task, author, null, ct);

    /// <summary>
    /// Queues a task's change for integration, attributed to the task and its author (WS-04), and waits for the result (WS-09); a copy
    /// that no longer holds <paramref name="submitted"/>, what <see cref="SealAsync"/> returned, is not integrated.
    /// </summary>
    public Task<IntegrationResult> IntegrateAsync(WorkingCopy copy, string task, string author, string? submitted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(copy);
        return queue.EnqueueAsync(copy, task, author, submitted, ct);
    }

    /// <summary>What the copy holds now, as integration would take it: the git tree of its files, ignored ones left out.</summary>
    public static Task<string> SealAsync(WorkingCopy copy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(copy);
        return IntegrationQueue.TreeAsync(copy.Directory, ct);
    }

    /// <summary>Removes the working copy and its branch when its task ends, unless the owner keeps working copies (WS-08).</summary>
    public async Task CloseWorkingCopyAsync(WorkingCopy copy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(copy);
        open.TryRemove(copy.Name, out _);
        if (!options.KeepWorkingCopies)
        {
            await Git.RunAsync(root, ct, "worktree", "remove", "--force", copy.Directory).ConfigureAwait(false);
            await Git.RunAsync(root, ct, "branch", "-D", copy.Branch).ConfigureAwait(false);
        }
    }

    async Task<IWorkingCopy> IWorkspace.OpenWorkingCopyAsync(string name, string agent, CancellationToken ct) =>
        await OpenWorkingCopyAsync(name, agent, ct).ConfigureAwait(false);

    Task IWorkspace.CloseWorkingCopyAsync(IWorkingCopy copy, CancellationToken ct) => CloseWorkingCopyAsync((WorkingCopy)copy, ct);

    Task<IntegrationResult> IWorkspace.IntegrateAsync(IWorkingCopy copy, string task, string author, string? submitted, CancellationToken ct) =>
        IntegrateAsync((WorkingCopy)copy, task, author, submitted, ct);

    Task<string> IWorkspace.SealAsync(IWorkingCopy copy, CancellationToken ct) => SealAsync((WorkingCopy)copy, ct);

    Task<IReadOnlyList<CopySnapshot>> IWorkspace.SnapshotAsync(CancellationToken ct) => SnapshotAsync(ct);

    Task IWorkspace.RestoreAsync(IReadOnlyList<CopySnapshot> snapshot, CancellationToken ct) => RestoreAsync(snapshot, ct);

    public void Dispose() => runLock.Dispose();

    /// <summary>The name of a working copy a run opens: its GUID id, then the agent.</summary>
    [GeneratedRegex("^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}-.+$")]
    private static partial Regex RunsCopy();

    private static Matcher Globs(IEnumerable<ProtectedPath> paths)
    {
        var matcher = new Matcher();
        matcher.AddIncludePatterns(paths.Select(path => path.Path));
        return matcher;
    }
}
