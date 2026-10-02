using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Workspace;

/// <summary>
/// The workspace's one integration queue (WS-09). Changes are integrated one at a time, in the order they arrive. Each is
/// rebased onto the baseline as it is when its turn comes, in a scratch worktree, then the baseline checks run there, and
/// only if they all pass does the baseline fast-forward to it (WS-02). A change that no longer applies is a conflict,
/// never resolved silently (WS-03): the baseline is merged into the working copy, with the conflicts marked in its files and
/// committed, so its author resolves them there and the next integration takes the resolved files.
/// </summary>
/// <param name="root">The workspace's top folder.</param>
/// <param name="baseline">The baseline branch.</param>
/// <param name="runId">The run whose changes are integrated, named in each commit (WS-04).</param>
/// <param name="checks">The baseline checks, by name, in order (WS-02).</param>
/// <param name="time">The clock for the waiting time.</param>
/// <param name="released">Called with the scratch worktree's folder before it is removed, such as to release what a sandbox set up for its checks.</param>
internal sealed class IntegrationQueue(string root, string baseline, string runId, IReadOnlyDictionary<string, ICheck> checks, TimeProvider time, Action<string>? released)
{
    private readonly Lock gate = new();
    private readonly Queue<DateTimeOffset> waiting = new();
    private Task tail = Task.CompletedTask;

    public IntegrationQueueStatus Status
    {
        get
        {
            lock (gate)
            {
                return new(waiting.Count, waiting.Count == 0 ? TimeSpan.Zero : time.GetUtcNow() - waiting.Peek());
            }
        }
    }

    public Task<IntegrationResult> EnqueueAsync(WorkingCopy copy, string task, string author, CancellationToken ct)
    {
        lock (gate)
        {
            waiting.Enqueue(time.GetUtcNow());

            // Each integration starts when the one before it ends, however that one ended.
            var turn = tail.ContinueWith(
                _ =>
                {
                    lock (gate)
                    {
                        waiting.Dequeue();
                    }

                    return IntegrateAsync(copy, task, author, ct);
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            tail = turn;
            return turn;
        }
    }

    private async Task<IntegrationResult> IntegrateAsync(WorkingCopy copy, string task, string author, CancellationToken ct)
    {
        // A change cancelled while it waited is dropped before git touches it.
        ct.ThrowIfCancellationRequested();
        var branchPoint = await CommitAsync(copy, task, author, ct).ConfigureAwait(false);
        if (await MarkedAsync(copy, branchPoint, ct).ConfigureAwait(false) is { Count: > 0 } unresolved)
        {
            // WS-03: a conflict is never resolved silently, so markers left in the change are a conflict still.
            return new(IntegrationOutcome.Conflict, unresolved);
        }

        var scratch = Path.Combine(root, WorkspaceOptions.StateFolder, "integration");
        await RemoveScratchAsync(scratch, ct).ConfigureAwait(false);
        await Git.RunAsync(root, ct, "worktree", "add", "--detach", scratch, copy.Branch).ConfigureAwait(false);
        IntegrationResult result;
        try
        {
            result = await RebaseAndCheckAsync(copy, scratch, ct).ConfigureAwait(false);
        }
        finally
        {
            released?.Invoke(scratch);
        }

        // The result stands whatever happens to the scratch folder; one left behind is removed before the next integration.
        var removed = await Git.TryRunAsync(root, CancellationToken.None, "worktree", "remove", "--force", scratch).ConfigureAwait(false);
        return removed.ExitCode == 0 ? result : result with { Warning = $"the integration's scratch folder {scratch} was not removed: {removed.Error.Trim()}" };
    }

    private async Task<IntegrationResult> RebaseAndCheckAsync(WorkingCopy copy, string scratch, CancellationToken ct)
    {
        if ((await Git.TryRunAsync(scratch, ct, "rebase", "--no-verify", baseline).ConfigureAwait(false)).ExitCode != 0)
        {
            var conflicts = await Git.RunAsync(scratch, ct, "diff", "--name-only", "--diff-filter=U").ConfigureAwait(false);
            await Git.RunAsync(scratch, ct, "rebase", "--abort").ConfigureAwait(false);
            await MergeBaselineAsync(copy, ct).ConfigureAwait(false);
            return new(IntegrationOutcome.Conflict, Lines(conflicts));
        }

        foreach (var (name, check) in checks)
        {
            var result = await check.RunAsync(new CheckContext(scratch, null, []), ct).ConfigureAwait(false);
            if (!result.Passed)
            {
                return new(IntegrationOutcome.ChecksFailed, [.. result.Findings.Select(finding => $"{name}: {finding}")]);
            }
        }

        var head = (await Git.RunAsync(scratch, ct, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        await Git.RunAsync(root, ct, "merge", "--ff-only", head).ConfigureAwait(false);
        return new(IntegrationOutcome.Integrated, []);
    }

    /// <summary>A scratch folder an earlier integration could not remove goes first, locked or not, so it never blocks the queue.</summary>
    private async Task RemoveScratchAsync(string scratch, CancellationToken ct)
    {
        if (!Directory.Exists(scratch))
        {
            return;
        }

        await Git.TryRunAsync(root, ct, "worktree", "remove", "--force", "--force", scratch).ConfigureAwait(false);
        if (Directory.Exists(scratch))
        {
            Directory.Delete(scratch, recursive: true);
        }

        await Git.RunAsync(root, ct, "worktree", "prune").ConfigureAwait(false);
    }

    /// <summary>The files whose change adds a line that starts or ends a conflict, as git marks them.</summary>
    private static async Task<List<string>> MarkedAsync(WorkingCopy copy, string branchPoint, CancellationToken ct)
    {
        var marked = new List<string>();
        string? file = null;
        foreach (var line in (await Git.RunAsync(copy.Directory, ct, "diff", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", "-U0", branchPoint, "HEAD").ConfigureAwait(false)).Split('\n'))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                file = line.StartsWith("+++ b/", StringComparison.Ordinal) ? line[6..].TrimEnd('\r') : null;
            }
            else if (file is not null && (line.StartsWith("+<<<<<<< ", StringComparison.Ordinal) || line.StartsWith("+>>>>>>> ", StringComparison.Ordinal)) && !marked.Contains(file))
            {
                marked.Add(file);
            }
        }

        return marked;
    }

    /// <summary>
    /// WS-03: brings the baseline into the working copy, which its change no longer applies to. Git marks the conflicts in the files,
    /// and the merge is committed with them, so the author sees and resolves them; the next integration squashes from the baseline
    /// as merged, so it takes only the author's resolved change.
    /// </summary>
    private async Task MergeBaselineAsync(WorkingCopy copy, CancellationToken ct)
    {
        string[] identity = ["-c", "user.name=Officina", "-c", "user.email=officina@localhost", "-c", "commit.gpgsign=false"];
        if ((await Git.TryRunAsync(copy.Directory, ct, [.. identity, "merge", "--no-verify", "--no-edit", baseline]).ConfigureAwait(false)).ExitCode != 0)
        {
            await Git.RunAsync(copy.Directory, ct, "add", "--all").ConfigureAwait(false);
            await Git.RunAsync(copy.Directory, ct, [.. identity, "commit", "--no-verify", "--message", "Officina: the baseline merged, with conflicts to resolve"]).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Commits what the agent changed as one commit, attributed to the run, the agent and the task (WS-04). The checkpoint
    /// commits on the branch (RUN-03) are squashed into it, so the baseline's history holds none of them.
    /// </summary>
    /// <returns>The commit the change is on top of.</returns>
    private async Task<string> CommitAsync(WorkingCopy copy, string task, string author, CancellationToken ct)
    {
        await Git.RunAsync(copy.Directory, ct, "add", "--all").ConfigureAwait(false);
        var branchPoint = (await Git.RunAsync(copy.Directory, ct, "merge-base", "HEAD", baseline).ConfigureAwait(false)).Trim();
        await Git.RunAsync(copy.Directory, ct, "reset", "--soft", branchPoint).ConfigureAwait(false);
        if ((await Git.TryRunAsync(copy.Directory, ct, "diff", "--cached", "--quiet").ConfigureAwait(false)).ExitCode != 0)
        {
            await Git.RunAsync(copy.Directory, ct, "commit", "--no-verify", "--author", $"{author} <{author}@officina>",
                "-m", $"Task {task}", "-m", $"Officina-Run: {runId}\nOfficina-Agent: {author}\nOfficina-Task: {task}").ConfigureAwait(false);
        }

        return branchPoint;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>How long the integration queue is, for the owner and the lead (WS-09).</summary>
/// <param name="Length">The changes waiting, not counting the one being integrated.</param>
/// <param name="LongestWait">How long the oldest of them has waited.</param>
public sealed record IntegrationQueueStatus(int Length, TimeSpan LongestWait);
