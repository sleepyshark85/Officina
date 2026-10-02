using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using static Sleepyshark.Officina.Workspace.Tests.Repository;

namespace Sleepyshark.Officina.Workspace.Tests;

/// <summary>Snapshots of working copies, restoring them, and cleaning up after a run that died (RUN-04, RUN-08), on a real git repository.</summary>
public class SnapshotTests
{
    // RUN-04, DESIGN.md §8.
    [Fact]
    public async Task A_snapshot_commits_changes_not_yet_committed_and_a_restore_returns_the_copy_to_it()
    {
        using var repository = await CreateAsync(("a.txt", "one\n"));
        using var workspace = await repository.OpenAsync();
        var copy = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);
        await Write(copy, "a.txt", "two\n");
        await Write(copy, "new.txt", "new\n");

        var snapshot = Assert.Single(await workspace.SnapshotAsync(Ct));
        await Write(copy, "a.txt", "three\n");
        await Write(copy, "later.txt", "later\n");
        await workspace.RestoreAsync([snapshot], Ct);

        Assert.Equal(("t1", "alice"), (snapshot.TaskId, snapshot.Agent));
        Assert.Equal(snapshot.Commit, (await repository.GitAsync("rev-parse", "agent/t1")).Trim());
        Assert.Equal(("two\n", "new\n", false), (File.ReadAllText(Path.Combine(copy.Directory, "a.txt")), File.ReadAllText(Path.Combine(copy.Directory, "new.txt")), File.Exists(Path.Combine(copy.Directory, "later.txt"))));
        Assert.Equal("one\n", repository.Baseline("a.txt")); // the baseline never sees any of it
    }

    [Fact]
    public async Task A_snapshot_of_an_unchanged_copy_adds_no_commit()
    {
        using var repository = await CreateAsync();
        using var workspace = await repository.OpenAsync();
        await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);
        var before = (await repository.GitAsync("rev-parse", "agent/t1")).Trim();

        var snapshot = Assert.Single(await workspace.SnapshotAsync(Ct));

        Assert.Equal(before, snapshot.Commit);
    }

    // RUN-04: a new process has no working copies open; the restore brings them back from the branches.
    [Fact]
    public async Task A_restore_in_a_new_process_reopens_a_copy_from_its_branch_and_a_gone_branch_from_its_commit()
    {
        using var repository = await CreateAsync(("a.txt", "one\n"));
        IReadOnlyList<CopySnapshot> snapshots;
        using (var first = await repository.OpenAsync())
        {
            var alice = await first.OpenWorkingCopyAsync("t1", "alice", Ct);
            var bob = await first.OpenWorkingCopyAsync("t2", "bob", Ct);
            await Write(alice, "a.txt", "alice\n");
            await Write(bob, "a.txt", "bob\n");
            snapshots = await first.SnapshotAsync(Ct);
            await Write(alice, "a.txt", "lost\n"); // work after the checkpoint, when the process dies
        }

        await repository.GitAsync("worktree", "remove", "--force", Path.Combine(repository.Root, ".sof", "worktrees", "t2"));
        await repository.GitAsync("branch", "-D", "agent/t2"); // bob's branch is gone too, so only the commit is left
        using var second = await repository.OpenAsync();
        await second.RestoreAsync(snapshots, Ct);

        Assert.Equal(["t1", "t2"], second.OpenCopies.Select(copy => copy.TaskId).Order());
        var (alice2, bob2) = (await second.OpenWorkingCopyAsync("t1", "alice", Ct), await second.OpenWorkingCopyAsync("t2", "bob", Ct));
        Assert.Equal(("alice\n", "bob\n"), (await alice2.ReadAsync("a.txt", ct: Ct), await bob2.ReadAsync("a.txt", ct: Ct)));
    }

    [Fact]
    public async Task A_restore_removes_a_copy_that_was_not_in_the_snapshot()
    {
        using var repository = await CreateAsync();
        using var workspace = await repository.OpenAsync();
        var snapshot = await workspace.SnapshotAsync(Ct);
        var added = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);

        await workspace.RestoreAsync(snapshot, Ct);

        Assert.Empty(workspace.OpenCopies);
        Assert.False(Directory.Exists(added.Directory));
    }

    // The follow-up from S14: a run that died leaves its worktrees behind.
    [Fact]
    public async Task Leftover_working_copies_are_removed_and_so_are_branches_of_runs_that_cannot_resume()
    {
        using var repository = await CreateAsync();
        using (var dead = await repository.OpenAsync(runId: "dead"))
        {
            await dead.OpenWorkingCopyAsync($"{Dead}-alice", "alice", Ct);
            await dead.OpenWorkingCopyAsync($"{Resumable}-bob", "bob", Ct);
        }

        await repository.GitAsync("branch", "agent/new-feature"); // the owner's own branch

        using var next = await repository.OpenAsync(runId: "next");
        var kept = new List<string>();
        await next.RemoveLeftoversAsync(task =>
        {
            kept.Add(task);
            return Task.FromResult(task.StartsWith(Resumable, StringComparison.Ordinal));
        }, null, Ct);

        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(repository.Root, ".sof", "worktrees")));
        Assert.Equal(
            [$"agent/{Resumable}-bob", "agent/new-feature"],
            (await repository.GitAsync("branch", "--list", "agent/*")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Equal([$"{Dead}-alice", $"{Resumable}-bob"], kept.Order()); // the owner's branch was never even asked about
    }

    // WS-08.
    [Fact]
    public async Task Working_copies_the_owner_keeps_are_not_leftovers()
    {
        using var repository = await CreateAsync();
        using (var ended = await repository.OpenAsync(new WorkspaceOptions { KeepWorkingCopies = true }, runId: "ended"))
        {
            var copy = await ended.OpenWorkingCopyAsync($"{Dead}-alice", "alice", Ct);
            await copy.WriteAsync("kept.txt", "kept", Ct);
            await ended.CloseWorkingCopyAsync(copy, Ct);
        }

        using var next = await repository.OpenAsync(new WorkspaceOptions { KeepWorkingCopies = true }, runId: "next");
        await next.RemoveLeftoversAsync(_ => Task.FromResult(false), null, Ct);

        Assert.Equal("kept", File.ReadAllText(Path.Combine(repository.Root, ".sof", "worktrees", $"{Dead}-alice", "kept.txt")));
        Assert.Contains($"agent/{Dead}-alice", await repository.GitAsync("branch", "--list", "agent/*"), StringComparison.Ordinal);
    }

    private const string Dead = "0198a000-0000-7000-8000-000000000001";
    private const string Resumable = "0198a000-0000-7000-8000-000000000002";

    [Fact]
    public async Task A_copy_the_run_holds_is_not_a_leftover()
    {
        using var repository = await CreateAsync();
        using var workspace = await repository.OpenAsync();
        var copy = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);

        await workspace.RemoveLeftoversAsync(_ => Task.FromResult(false), null, Ct);

        Assert.True(Directory.Exists(copy.Directory));
        Assert.Contains("agent/t1", await repository.GitAsync("branch", "--list", "agent/*"), StringComparison.Ordinal);
    }

    // WS-04: the baseline's history holds the change, not the checkpoints.
    [Fact]
    public async Task Integrating_a_change_squashes_its_checkpoint_commits_into_one()
    {
        using var repository = await CreateAsync(("a.txt", "one\n"));
        using var workspace = await repository.OpenAsync();
        var copy = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);
        await Write(copy, "a.txt", "two\n");
        await workspace.SnapshotAsync(Ct);
        await Write(copy, "b.txt", "new\n");
        await workspace.SnapshotAsync(Ct);

        Assert.Equal(IntegrationOutcome.Integrated, (await workspace.IntegrateAsync(copy, Ct)).Outcome);

        Assert.Equal("Task t1\nStart\n", await repository.GitAsync("log", "--format=%s"), ignoreLineEndingDifferences: true);
        Assert.Equal(("two\n", "new\n"), (repository.Baseline("a.txt"), repository.Baseline("b.txt")));
    }

    private static async Task Write(WorkingCopy copy, string path, string text)
    {
        if (File.Exists(Path.Combine(copy.Directory, path)))
        {
            await copy.ReadAsync(path, ct: Ct);
        }

        await copy.WriteAsync(path, text, Ct);
    }
}
