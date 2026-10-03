using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using static Sleepyshark.Officina.Workspace.Tests.Repository;

namespace Sleepyshark.Officina.Workspace.Tests;

/// <summary>How an agent reaches files in its working copy (WS-05, WS-06, WS-07).</summary>
public sealed class WorkingCopyTests : IAsyncLifetime
{
    private Repository repository = null!;
    private GitWorkspace workspace = null!;
    private WorkingCopy copy = null!;

    public async ValueTask InitializeAsync()
    {
        repository = await CreateAsync(
            ("src/a.txt", "one\ntwo\nthree\nfour\n"), (".env", "KEY=secret\n"), ("src/.env.local", "KEY=secret\n"),
            ("secrets/key.txt", "secret\n"), (".sof/artifacts/1", "secret\n"), ("sof.json", "{}\n"), ("sof.local.json", "{}\n"), ("software.json", "{}\n"), ("docs/guide.md", "secret-free\n"));
        var protectedPaths = new ProtectedPath[] { new() { Path = "secrets/**" }, new() { Path = "docs/**", Access = PathAccess.ReadOnly } };
        workspace = await repository.OpenAsync(new WorkspaceOptions { ProtectedPaths = protectedPaths });
        copy = await workspace.OpenWorkingCopyAsync("t1", "alice", Ct);
    }

    public ValueTask DisposeAsync()
    {
        workspace.Dispose();
        repository.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task An_agent_reads_some_lines_of_a_file_and_searches_the_files_it_can_see()
    {
        await copy.WriteAsync("src/b.txt", "secret two\n", Ct);

        Assert.Equal("two\nthree", await copy.ReadAsync("src/a.txt", 2, 2, Ct));
        Assert.Equal(
            [new("docs/guide.md", 1, "secret-free"), new("src/b.txt", 1, "secret two")],
            await copy.SearchAsync("sec.*t", Ct));
    }

    [Fact]
    public async Task An_edit_replaces_the_one_place_it_states()
    {
        await copy.ReadAsync("src/a.txt", 1, 1, Ct);
        await copy.EditAsync("src/a.txt", "two", "2", Ct);
        await copy.EditAsync("src/a.txt", "four", "4", Ct);

        Assert.Equal("one\n2\nthree\n4\n", await copy.ReadAsync("src/a.txt", ct: Ct));
        var ambiguous = await Assert.ThrowsAsync<WorkspaceException>(() => copy.EditAsync("src/a.txt", "e", "E", Ct));
        Assert.Equal("The text to replace must occur exactly once in src/a.txt, but it occurs more than once.", ambiguous.Message);
    }

    [Fact]
    public async Task An_edit_fails_if_the_file_changed_since_the_agent_read_it()
    {
        var unread = await Assert.ThrowsAsync<WorkspaceException>(() => copy.EditAsync("src/a.txt", "two", "2", Ct));
        await copy.ReadAsync("src/a.txt", ct: Ct);
        await File.WriteAllTextAsync(Path.Combine(copy.Directory, "src", "a.txt"), "one\ntwo\n", Ct);

        var changed = await Assert.ThrowsAsync<WorkspaceException>(() => copy.EditAsync("src/a.txt", "two", "2", Ct));
        await Assert.ThrowsAsync<WorkspaceException>(() => copy.WriteAsync("src/a.txt", "overwritten", Ct));

        Assert.Equal(unread.Message, changed.Message);
        Assert.Equal("src/a.txt changed since you last read it, or you have not read it. Read it again first.", changed.Message);
        Assert.Equal("one\ntwo\n", await copy.ReadAsync("src/a.txt", ct: Ct));
    }

    [Fact]
    public async Task A_file_the_agent_has_read_can_be_deleted_or_moved_and_others_cannot()
    {
        var unread = await Assert.ThrowsAsync<WorkspaceException>(() => copy.DeleteAsync("src/a.txt", Ct));
        await Assert.ThrowsAsync<WorkspaceException>(() => copy.MoveAsync("src/a.txt", "src/b.txt", Ct));
        await copy.ReadAsync("src/a.txt", ct: Ct);

        await copy.MoveAsync("src/a.txt", "moved/b.txt", Ct);

        Assert.Equal("src/a.txt changed since you last read it, or you have not read it. Read it again first.", unread.Message);
        Assert.Equal("one\ntwo\nthree\nfour\n", await copy.ReadAsync("moved/b.txt", ct: Ct));
        Assert.False(File.Exists(Path.Combine(copy.Directory, "src", "a.txt")));
        await copy.DeleteAsync("moved/b.txt", Ct);
        var gone = await Assert.ThrowsAsync<WorkspaceException>(() => copy.ReadAsync("moved/b.txt", ct: Ct));
        Assert.Equal("moved/b.txt does not exist.", gone.Message);
    }

    [Fact]
    public async Task A_move_never_replaces_a_file_and_neither_reaches_protected_paths()
    {
        await copy.ReadAsync("src/a.txt", ct: Ct);
        await copy.ReadAsync("sof.json", ct: Ct);

        var exists = await Assert.ThrowsAsync<WorkspaceException>(() => copy.MoveAsync("src/a.txt", "sof.json", Ct));
        var readOnly = await Assert.ThrowsAsync<WorkspaceException>(() => copy.DeleteAsync("sof.json", Ct));
        var hidden = await Assert.ThrowsAsync<WorkspaceException>(() => copy.MoveAsync("src/a.txt", ".env", Ct));

        Assert.Equal(["sof.json is read-only.", "sof.json is read-only.", ".env does not exist."], [exists.Message, readOnly.Message, hidden.Message]);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("src/.env.local")]
    [InlineData("secrets/key.txt")]
    [InlineData(".git")]
    [InlineData(".sof/artifacts/1")] // STO-01: the storage's artifacts, in the state folder
    public async Task A_hidden_path_looks_like_it_does_not_exist(string path)
    {
        var read = await Assert.ThrowsAsync<WorkspaceException>(() => copy.ReadAsync(path, ct: Ct));
        var write = await Assert.ThrowsAsync<WorkspaceException>(() => copy.WriteAsync(path, "x", Ct));

        Assert.Equal((read.Message, write.Message), ($"{path} does not exist.", $"{path} does not exist."));
        Assert.Empty(await copy.SearchAsync("KEY|secret$", Ct));
    }

    [Theory]
    [InlineData("sof.json")]
    [InlineData("sof.local.json")]
    [InlineData("docs/guide.md")]
    public async Task A_read_only_path_can_be_read_but_not_changed(string path)
    {
        var text = await copy.ReadAsync(path, ct: Ct);

        var write = await Assert.ThrowsAsync<WorkspaceException>(() => copy.WriteAsync(path, "x", Ct));
        await Assert.ThrowsAsync<WorkspaceException>(() => copy.EditAsync(path, text, "x", Ct));
        Assert.Equal($"{path} is read-only.", write.Message);
    }

    [Fact]
    public async Task Only_the_configuration_files_are_read_only_not_others_that_start_alike()
    {
        await copy.ReadAsync("software.json", ct: Ct);
        await copy.WriteAsync("software.json", "[]", Ct);

        Assert.Equal("[]", await copy.ReadAsync("software.json", ct: Ct));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    public async Task Files_outside_the_working_copy_cannot_be_reached(string path)
    {
        var write = await Assert.ThrowsAsync<WorkspaceException>(() => copy.WriteAsync(path, "x", Ct));

        Assert.Equal($"{path} is outside the workspace.", write.Message);
        Assert.False(File.Exists(Path.Combine(copy.Directory, path)));
    }

    [Fact]
    public async Task A_link_cannot_lead_outside_the_working_copy()
    {
        var outside = Path.Combine(repository.Root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "f.txt"), "outside\n", Ct);
        TryLink(Path.Combine(copy.Directory, "link.txt"), Path.Combine(outside, "f.txt"));
        TryLink(Path.Combine(copy.Directory, "linked"), outside);
        await copy.ReadAsync("src/a.txt", ct: Ct);

        var refused = new List<string>();
        foreach (var call in new Func<Task>[]
        {
            () => copy.ReadAsync("link.txt", ct: Ct), () => copy.ReadAsync("linked/f.txt", ct: Ct),
            () => copy.WriteAsync("link.txt", "x", Ct), () => copy.WriteAsync("linked/new.txt", "x", Ct), () => copy.WriteAsync("linked/new/f.txt", "x", Ct),
            () => copy.DeleteAsync("linked/f.txt", Ct), () => copy.MoveAsync("linked/f.txt", "f.txt", Ct), () => copy.MoveAsync("src/a.txt", "linked/a.txt", Ct),
        })
        {
            refused.Add((await Assert.ThrowsAsync<WorkspaceException>(call)).Message);
        }

        Assert.Equal(
            ["link.txt", "linked/f.txt", "link.txt", "linked/new.txt", "linked/new/f.txt", "linked/f.txt", "linked/f.txt", "linked/a.txt"],
            refused.Select(message => message.Replace(" is outside the workspace.", "", StringComparison.Ordinal)));
        Assert.Equal(["f.txt"], Directory.GetFileSystemEntries(outside).Select(Path.GetFileName));
        Assert.Equal("outside\n", await File.ReadAllTextAsync(Path.Combine(outside, "f.txt"), Ct));
    }

    // A process in the sandbox may replace a folder with a link while the host reads or writes the copy. Each test races
    // calls against a folder that keeps turning into a link to one outside and back; nothing outside may be read or changed.
    [Fact]
    public async Task A_write_racing_a_folder_replaced_by_a_link_stays_in_the_working_copy()
    {
        var outside = await RaceAsync(i => copy.WriteAsync($"race/w{i}.txt", "x", Ct));

        Assert.Equal(["f.txt"], Directory.GetFileSystemEntries(outside).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_read_racing_a_folder_replaced_by_a_link_stays_in_the_working_copy()
    {
        var read = new List<string>();
        await RaceAsync(async _ => read.Add(await copy.ReadAsync("race/f.txt", ct: Ct)));

        Assert.DoesNotContain("outside\n", read);
    }

    [Fact]
    public async Task A_delete_or_move_racing_a_folder_replaced_by_a_link_stays_in_the_working_copy()
    {
        // The file to delete or move has the content of the one outside, so having read either lets the call go ahead.
        var outside = await RaceAsync(async i =>
        {
            await copy.WriteAsync($"m{i}.txt", "outside\n", Ct);
            await Attempt(() => copy.WriteAsync("race/f.txt", "outside\n", Ct));
            await Attempt(() => copy.ReadAsync("race/f.txt", ct: Ct));
            await ((i % 3) switch
            {
                0 => copy.DeleteAsync("race/f.txt", Ct),
                1 => copy.MoveAsync("race/f.txt", $"moved/f{i}.txt", Ct),
                _ => copy.MoveAsync($"m{i}.txt", $"race/m{i}.txt", Ct),
            });
        });

        Assert.Equal(["f.txt"], Directory.GetFileSystemEntries(outside).Select(Path.GetFileName));
        Assert.Equal("outside\n", await File.ReadAllTextAsync(Path.Combine(outside, "f.txt"), Ct));
    }

    /// <summary>
    /// Repeats a call, for a bounded time, while another thread keeps replacing the folder <c>race</c> of the copy with a
    /// link to a folder outside it, and back. The call may fail, as the folder may be a link when it runs, but some calls
    /// must get through. Returns the folder outside.
    /// </summary>
    private async Task<string> RaceAsync(Func<int, Task> call)
    {
        var outside = Path.Combine(repository.Root, "outside");
        var race = Path.Combine(copy.Directory, "race");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "f.txt"), "outside\n", Ct);
        TryLink(race, outside);

        using var stop = new CancellationTokenSource();
        var flipper = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (new DirectoryInfo(race).LinkTarget is null)
                    {
                        if (Directory.Exists(race))
                        {
                            Directory.Delete(race, recursive: true);
                        }

                        Directory.CreateSymbolicLink(race, outside);
                    }
                    else
                    {
                        Directory.Delete(race);
                        Directory.CreateDirectory(race);
                        File.WriteAllText(Path.Combine(race, "f.txt"), "inside\n");
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The host holds the folder, or is writing in it; try again.
                }
            }
        }, Ct);

        var (watch, succeeded) = (System.Diagnostics.Stopwatch.StartNew(), 0);
        for (var i = 0; watch.Elapsed < TimeSpan.FromSeconds(2); i++)
        {
            succeeded += await Attempt(() => call(i)) ? 1 : 0;
        }

        await stop.CancelAsync();
        await flipper;
        Assert.True(succeeded > 0, "No call got through while the folder was real.");
        return outside;
    }

    private static async Task<bool> Attempt(Func<Task> call)
    {
        try
        {
            await call();
            return true;
        }
        catch (Exception exception) when (exception is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            // Refused while the folder was a link, or missing while it was being replaced.
            return false;
        }
    }

    private static void TryLink(string path, string target)
    {
        try
        {
            if (Directory.Exists(target))
            {
                Directory.CreateSymbolicLink(path, target);
            }
            else
            {
                File.CreateSymbolicLink(path, target);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Creating links needs a privilege this machine does not grant.");
        }
    }
}
