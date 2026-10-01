using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using static Sleepyshark.Officina.Workspace.Tests.Repository;

namespace Sleepyshark.Officina.Workspace.Tests;

/// <summary>Working copies, the integration queue and the run lock, on a real git repository.</summary>
public class GitWorkspaceTests
{
    [Fact]
    public async Task Changes_stay_invisible_to_other_agents_until_integrated()
    {
        using var repository = await CreateAsync(("src/a.txt", "one\n"));
        using var workspace = await repository.OpenAsync();
        var alice = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);
        var bob = await workspace.OpenWorkingCopyAsync("t2", "bob", Ct);

        await EditAsync(alice, "src/a.txt", "one", "two");

        Assert.Equal(("one\n", "one\n"), (await bob.ReadAsync("src/a.txt", ct: Ct), repository.Baseline("src/a.txt")));
        Assert.Equal(IntegrationOutcome.Integrated, (await workspace.IntegrateAsync(alice, Ct)).Outcome);
        var carol = await workspace.OpenWorkingCopyAsync("t3", "carol", Ct);
        Assert.Equal(("two\n", "two\n"), (repository.Baseline("src/a.txt"), await carol.ReadAsync("src/a.txt", ct: Ct)));
    }

    [Fact]
    public async Task Each_integrated_change_is_a_commit_attributed_to_its_run_agent_and_task()
    {
        using var repository = await CreateAsync(("a.txt", "one\n"));
        using var workspace = await repository.OpenAsync(runId: "run-7");
        var alice = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);
        await EditAsync(alice, "a.txt", "one", "two");

        await workspace.IntegrateAsync(alice, Ct);

        Assert.Equal(
            "alice\nTask t1\n\nOfficina-Run: run-7\nOfficina-Agent: alice\nOfficina-Task: t1",
            (await repository.GitAsync("log", "-1", "--format=%an%n%B")).TrimEnd(), ignoreLineEndingDifferences: true);
    }

    // TEST-22, first half.
    [Fact]
    public async Task Two_agents_editing_the_same_lines_produce_a_conflict_never_an_overwrite()
    {
        using var repository = await CreateAsync(("a.txt", "one\n"));
        using var workspace = await repository.OpenAsync();
        var alice = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);
        var bob = await workspace.OpenWorkingCopyAsync("t2", "bob", Ct);
        await EditAsync(alice, "a.txt", "one", "alice's");
        await EditAsync(bob, "a.txt", "one", "bob's");

        await workspace.IntegrateAsync(alice, Ct);
        var result = await workspace.IntegrateAsync(bob, Ct);

        Assert.Equal((IntegrationOutcome.Conflict, "a.txt"), (result.Outcome, Assert.Single(result.Details)));
        Assert.Equal("alice's\n", repository.Baseline("a.txt"));
    }

    [Fact]
    public async Task A_change_is_checked_against_the_baseline_as_it_is_when_its_turn_comes()
    {
        using var repository = await CreateAsync(("a.txt", "one\ntwo\nthree\n"));
        var checkedFiles = new List<string>();
        var check = new Check(async context =>
        {
            checkedFiles.Add(await File.ReadAllTextAsync(Path.Combine(context.Directory!, "a.txt"), Ct));
            return new(true, []);
        });
        using var workspace = await repository.OpenAsync(checks: new() { ["build"] = check });
        var alice = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);
        var bob = await workspace.OpenWorkingCopyAsync("t2", "bob", Ct);
        await EditAsync(alice, "a.txt", "one", "1");
        await EditAsync(bob, "a.txt", "three", "3");

        await workspace.IntegrateAsync(alice, Ct);
        await workspace.IntegrateAsync(bob, Ct);

        Assert.Equal(["1\ntwo\nthree\n", "1\ntwo\n3\n"], checkedFiles);
        Assert.Equal("1\ntwo\n3\n", repository.Baseline("a.txt"));
    }

    // TEST-22, second half.
    [Fact]
    public async Task A_change_that_breaks_the_baseline_checks_is_rejected_and_the_baseline_stays()
    {
        using var repository = await CreateAsync(("a.txt", "one\n"));
        var build = new Check(async context => (await File.ReadAllTextAsync(Path.Combine(context.Directory!, "a.txt"), Ct)).Contains("broken", StringComparison.Ordinal)
            ? new(false, ["a.txt does not compile"])
            : new(true, []));
        using var workspace = await repository.OpenAsync(checks: new() { ["build"] = Check.Passing, ["tests"] = build });
        var alice = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);
        await EditAsync(alice, "a.txt", "one", "broken");

        var result = await workspace.IntegrateAsync(alice, Ct);

        Assert.Equal((IntegrationOutcome.ChecksFailed, "tests: a.txt does not compile"), (result.Outcome, Assert.Single(result.Details)));
        Assert.Equal("one\n", repository.Baseline("a.txt"));
    }

    [Fact]
    public async Task Integrations_wait_in_one_queue_in_order_and_its_length_and_waiting_time_are_visible()
    {
        using var repository = await CreateAsync();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var check = new Check(async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return new(true, []);
        });
        using var workspace = await repository.OpenAsync(checks: new() { ["build"] = check });
        var copies = new List<WorkingCopy>();
        foreach (var agent in new[] { "alice", "bob", "carol" })
        {
            var copy = await workspace.OpenWorkingCopyAsync($"task-{agent}", agent, Ct);
            await copy.WriteAsync($"{agent}.txt", agent, Ct);
            copies.Add(copy);
        }

        var first = workspace.IntegrateAsync(copies[0], Ct);
        await started.Task;
        var second = workspace.IntegrateAsync(copies[1], Ct);
        repository.Time.Advance(TimeSpan.FromMinutes(5));
        var third = workspace.IntegrateAsync(copies[2], Ct);

        Assert.Equal(new IntegrationQueueStatus(2, TimeSpan.FromMinutes(5)), workspace.Queue);
        release.SetResult();
        await Task.WhenAll(first, second, third);
        Assert.Equal(new IntegrationQueueStatus(0, TimeSpan.Zero), workspace.Queue);
        Assert.Equal("carol\nbob\nalice\nowner\n", await repository.GitAsync("log", "--format=%an"), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public async Task A_second_run_on_the_same_workspace_is_refused_with_the_active_runs_name()
    {
        using var repository = await CreateAsync();
        var first = await repository.OpenAsync(runId: "run-1");

        var refused = await Assert.ThrowsAsync<WorkspaceException>(() => repository.OpenAsync(runId: "run-2"));
        first.Dispose();
        using var next = await repository.OpenAsync(runId: "run-2");

        Assert.Equal("Run run-1 is already active on this workspace. Start this run when it ends.", refused.Message);
    }

    [Fact]
    public async Task A_run_holding_the_lock_before_writing_its_name_is_still_refused()
    {
        using var repository = await CreateAsync();
        using var held = new FileStream(Path.Combine(Directory.CreateDirectory(Path.Combine(repository.Root, ".sof")).FullName, "run.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var refused = await Assert.ThrowsAsync<WorkspaceException>(() => repository.OpenAsync());

        Assert.Equal("Another run is already active on this workspace. Start this run when it ends.", refused.Message);
    }

    [Fact]
    public async Task A_cancelled_integration_leaves_the_working_copy_untouched()
    {
        using var repository = await CreateAsync();
        using var workspace = await repository.OpenAsync();
        var copy = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);
        await copy.WriteAsync("new.txt", "new", Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.IntegrateAsync(copy, new CancellationToken(canceled: true)));

        Assert.Equal("?? new.txt", (await Git.RunAsync(copy.Directory, Ct, "status", "--porcelain")).Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_working_copy_is_removed_when_its_task_ends_unless_the_owner_keeps_them(bool keep)
    {
        using var repository = await CreateAsync();
        using var workspace = await repository.OpenAsync(new WorkspaceOptions { KeepWorkingCopies = keep });
        var copy = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);

        await workspace.CloseWorkingCopyAsync(copy, Ct);

        Assert.Equal((keep, keep), (Directory.Exists(copy.Directory), (await repository.GitAsync("branch", "--list", "agent/t1")).Length > 0));
    }

    private static async Task EditAsync(WorkingCopy copy, string path, string oldText, string newText)
    {
        await copy.ReadAsync(path, ct: Ct);
        await copy.EditAsync(path, oldText, newText, Ct);
    }
}
