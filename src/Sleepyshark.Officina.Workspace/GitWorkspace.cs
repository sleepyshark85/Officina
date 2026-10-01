using Microsoft.Extensions.FileSystemGlobbing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Workspace;

/// <summary>
/// A run's hold on a git workspace (DESIGN.md §7). The baseline is the branch checked out at the root. Each agent
/// works in its own worktree on <c>agent/&lt;task&gt;</c>, and changes reach the baseline only through the integration
/// queue. One run holds a workspace at a time (RUN-12); disposing it lets the next run in.
/// </summary>
public sealed class GitWorkspace : IDisposable
{
    /// <summary>The folder at the root where the workspace keeps its lock and worktrees.</summary>
    internal const string StateFolder = ".sof";

    // Git's own files, secrets files (INV-06) and Officina's state are hidden, and its configuration is read-only (INV-10).
    // Configuration can add protected paths, never remove these.
    private static readonly ProtectedPath[] Fixed =
    [
        new() { Path = ".git" },
        new() { Path = ".git/**" },
        new() { Path = "**/.env*" },
        new() { Path = $"{StateFolder}/**" },
        new() { Path = "sof*.json", Access = PathAccess.ReadOnly },
    ];

    private readonly string root;
    private readonly WorkspaceOptions options;
    private readonly FileStream runLock;
    private readonly IntegrationQueue queue;
    private readonly Matcher hidden;
    private readonly Matcher readOnly;

    private GitWorkspace(string root, string baseline, string runId, WorkspaceOptions options, IReadOnlyDictionary<string, ICheck> baselineChecks, TimeProvider time, FileStream runLock)
    {
        this.root = root;
        this.options = options;
        this.runLock = runLock;
        queue = new IntegrationQueue(root, baseline, runId, baselineChecks, time);
        var paths = Fixed.Concat(options.ProtectedPaths).ToList();
        hidden = Globs(paths.Where(path => path.Access == PathAccess.Hidden));
        readOnly = Globs(paths.Where(path => path.Access == PathAccess.ReadOnly));
    }

    /// <summary>The integration queue's length and waiting time (WS-09).</summary>
    public IntegrationQueueStatus Queue => queue.Status;

    /// <summary>Takes the workspace for a run. A second run is refused while the first holds it (RUN-12).</summary>
    /// <param name="root">The top folder of a git repository, with the baseline branch checked out.</param>
    /// <param name="runId">The run, named to any other run that tries to open the workspace.</param>
    /// <param name="options">The workspace settings.</param>
    /// <param name="baselineChecks">The checks a change must pass on the baseline before it is integrated (WS-02), by name.</param>
    /// <param name="time">The clock for the queue's waiting time.</param>
    /// <param name="ct">Cancels opening.</param>
    /// <exception cref="WorkspaceException">Another run holds the workspace, or the root has no branch checked out.</exception>
    public static async Task<GitWorkspace> OpenAsync(
        string root, string runId, WorkspaceOptions options, IReadOnlyDictionary<string, ICheck> baselineChecks, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(baselineChecks);
        ArgumentNullException.ThrowIfNull(time);
        root = Path.GetFullPath(root);
        var state = Directory.CreateDirectory(Path.Combine(root, StateFolder)).FullName;
        var activeRun = Path.Combine(state, "run");
        FileStream runLock;
        try
        {
            // The operating system releases the lock if the process dies, so a crashed run never blocks the next one.
            runLock = new FileStream(Path.Combine(state, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new WorkspaceException($"Run {File.ReadAllText(activeRun)} is already active on this workspace. Start this run when it ends.");
        }

        try
        {
            await File.WriteAllTextAsync(activeRun, runId, ct).ConfigureAwait(false);
            var (exitCode, branch, _) = await Git.TryRunAsync(root, ct, "symbolic-ref", "--short", "HEAD").ConfigureAwait(false);
            return exitCode == 0
                ? new GitWorkspace(root, branch.Trim(), runId, options, baselineChecks, time, runLock)
                : throw new WorkspaceException($"{root} must be a git repository with the baseline branch checked out.");
        }
        catch
        {
            await runLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates the working copy where an agent does a task, from the baseline as it is now (WS-01).</summary>
    public async Task<WorkingCopy> OpenWorkingCopyAsync(string taskId, string agent, CancellationToken ct = default)
    {
        var copy = new WorkingCopy(taskId, agent, Path.Combine(root, StateFolder, "worktrees", taskId), hidden, readOnly);
        await Git.RunAsync(root, ct, "worktree", "add", "-b", copy.Branch, copy.Directory, "HEAD").ConfigureAwait(false);
        return copy;
    }

    /// <summary>Queues the working copy's changes for integration into the baseline, and waits for the result (WS-09).</summary>
    public Task<IntegrationResult> IntegrateAsync(WorkingCopy copy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(copy);
        return queue.EnqueueAsync(copy, ct);
    }

    /// <summary>Removes the working copy and its branch when its task ends, unless the owner keeps working copies (WS-08).</summary>
    public async Task CloseWorkingCopyAsync(WorkingCopy copy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (!options.KeepWorkingCopies)
        {
            await Git.RunAsync(root, ct, "worktree", "remove", "--force", copy.Directory).ConfigureAwait(false);
            await Git.RunAsync(root, ct, "branch", "-D", copy.Branch).ConfigureAwait(false);
        }
    }

    public void Dispose() => runLock.Dispose();

    private static Matcher Globs(IEnumerable<ProtectedPath> paths)
    {
        var matcher = new Matcher();
        matcher.AddIncludePatterns(paths.Select(path => path.Path));
        return matcher;
    }
}
