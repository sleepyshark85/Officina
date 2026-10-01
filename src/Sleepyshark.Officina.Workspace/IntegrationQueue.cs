using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Workspace;

/// <summary>
/// The workspace's one integration queue (WS-09). Changes are integrated one at a time, in the order they arrive. Each is
/// rebased onto the baseline as it is when its turn comes, in a scratch worktree, then the baseline checks run there, and
/// only if they all pass does the baseline fast-forward to it (WS-02). A change that no longer applies is a conflict,
/// never resolved silently (WS-03).
/// </summary>
internal sealed class IntegrationQueue(string root, string baseline, string runId, IReadOnlyDictionary<string, ICheck> checks, TimeProvider time)
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

    public Task<IntegrationResult> EnqueueAsync(WorkingCopy copy, CancellationToken ct)
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

                    return IntegrateAsync(copy, ct);
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            tail = turn;
            return turn;
        }
    }

    private async Task<IntegrationResult> IntegrateAsync(WorkingCopy copy, CancellationToken ct)
    {
        // A change cancelled while it waited is dropped before git touches it.
        ct.ThrowIfCancellationRequested();
        await CommitAsync(copy, ct).ConfigureAwait(false);
        var scratch = Path.Combine(root, WorkspaceOptions.StateFolder, "integration");
        await Git.RunAsync(root, ct, "worktree", "add", "--detach", scratch, copy.Branch).ConfigureAwait(false);
        try
        {
            if ((await Git.TryRunAsync(scratch, ct, "rebase", "--no-verify", baseline).ConfigureAwait(false)).ExitCode != 0)
            {
                var conflicts = await Git.RunAsync(scratch, ct, "diff", "--name-only", "--diff-filter=U").ConfigureAwait(false);
                await Git.RunAsync(scratch, ct, "rebase", "--abort").ConfigureAwait(false);
                return new(IntegrationOutcome.Conflict, Lines(conflicts));
            }

            foreach (var (name, check) in checks)
            {
                var result = await check.RunAsync(new CheckContext(scratch), ct).ConfigureAwait(false);
                if (!result.Passed)
                {
                    return new(IntegrationOutcome.ChecksFailed, [.. result.Findings.Select(finding => $"{name}: {finding}")]);
                }
            }

            var head = (await Git.RunAsync(scratch, ct, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
            await Git.RunAsync(root, ct, "merge", "--ff-only", head).ConfigureAwait(false);
            return new(IntegrationOutcome.Integrated, []);
        }
        finally
        {
            await Git.RunAsync(root, CancellationToken.None, "worktree", "remove", "--force", scratch).ConfigureAwait(false);
        }
    }

    /// <summary>Commits what the agent changed, attributed to the run, the agent and the task (WS-04).</summary>
    private async Task CommitAsync(WorkingCopy copy, CancellationToken ct)
    {
        await Git.RunAsync(copy.Directory, ct, "add", "--all").ConfigureAwait(false);
        if ((await Git.TryRunAsync(copy.Directory, ct, "diff", "--cached", "--quiet").ConfigureAwait(false)).ExitCode != 0)
        {
            await Git.RunAsync(copy.Directory, ct, "commit", "--no-verify", "--author", $"{copy.Agent} <{copy.Agent}@officina>",
                "-m", $"Task {copy.TaskId}", "-m", $"Officina-Run: {runId}\nOfficina-Agent: {copy.Agent}\nOfficina-Task: {copy.TaskId}").ConfigureAwait(false);
        }
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>How long the integration queue is, for the owner and the lead (WS-09).</summary>
/// <param name="Length">The changes waiting, not counting the one being integrated.</param>
/// <param name="LongestWait">How long the oldest of them has waited.</param>
public sealed record IntegrationQueueStatus(int Length, TimeSpan LongestWait);

/// <summary>How an integration ended.</summary>
/// <param name="Outcome">Whether the change reached the baseline.</param>
/// <param name="Details">For a conflict, the conflicting files; for failed checks, each finding after its check's name.</param>
public sealed record IntegrationResult(IntegrationOutcome Outcome, IReadOnlyList<string> Details);

public enum IntegrationOutcome
{
    /// <summary>The baseline now has the change.</summary>
    Integrated,

    /// <summary>The change no longer applies to the baseline; it goes back to the author or the lead (WS-03).</summary>
    Conflict,

    /// <summary>A baseline check failed on the change, so the baseline did not move (WS-02).</summary>
    ChecksFailed,
}
