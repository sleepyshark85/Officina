using Sleepyshark.Officina.Core.Configuration;
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
            ("secrets/key.txt", "secret\n"), ("sof.json", "{}\n"), ("sof.local.json", "{}\n"), ("software.json", "{}\n"), ("docs/guide.md", "secret-free\n"));
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

    [Theory]
    [InlineData(".env")]
    [InlineData("src/.env.local")]
    [InlineData("secrets/key.txt")]
    [InlineData(".git")]
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
        var outside = Path.Combine(repository.Root, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside\n", Ct);
        try
        {
            File.CreateSymbolicLink(Path.Combine(copy.Directory, "link.txt"), outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Creating links needs a privilege this machine does not grant.");
        }

        var read = await Assert.ThrowsAsync<WorkspaceException>(() => copy.ReadAsync("link.txt", ct: Ct));
        Assert.Equal("link.txt is outside the workspace.", read.Message);
    }
}
