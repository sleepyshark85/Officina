using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Workspace.Tests;

/// <summary>A real git repository in a temporary folder, with <c>main</c> checked out and one commit (DESIGN.md §11).</summary>
internal sealed class Repository : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("officina-").FullName;

    public FakeTimeProvider Time { get; } = new();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Repository> CreateAsync(params (string Path, string Text)[] files)
    {
        var repository = new Repository();
        await repository.GitAsync("init", "--initial-branch=main");
        await repository.GitAsync("config", "user.name", "owner");
        await repository.GitAsync("config", "user.email", "owner@example.com");
        await repository.GitAsync("config", "core.autocrlf", "false");
        foreach (var (path, text) in files.Append(("README.md", "A project.\n")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repository.Root, path))!);
            await File.WriteAllTextAsync(Path.Combine(repository.Root, path), text, Ct);
        }

        await repository.GitAsync("add", "--all");
        await repository.GitAsync("commit", "-m", "Start");
        return repository;
    }

    public Task<string> GitAsync(params string[] arguments) => Git.RunAsync(Root, Ct, arguments);

    /// <summary>A file of the baseline, as checked out at the root.</summary>
    public string Baseline(string path) => File.ReadAllText(Path.Combine(Root, path));

    public Task<GitWorkspace> OpenAsync(WorkspaceOptions? options = null, Dictionary<string, ICheck>? checks = null, string runId = "run-1") =>
        GitWorkspace.OpenAsync(Root, runId, options ?? new(), checks ?? [], Time, Ct);

    public void Dispose()
    {
        // Git makes its object files read-only, which stops Windows deleting them.
        foreach (var file in Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Root, recursive: true);
    }
}

/// <summary>A baseline check the test decides; real checks run commands in the sandbox (S15).</summary>
internal sealed class Check(Func<CheckContext, Task<CheckResult>> run) : ICheck
{
    public static Check Passing { get; } = new(_ => Task.FromResult(new CheckResult(true, [])));

    public async ValueTask<CheckResult> RunAsync(CheckContext context, CancellationToken ct) => await run(context);
}
