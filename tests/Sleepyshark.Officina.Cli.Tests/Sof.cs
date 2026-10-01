namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>Runs the real <c>sof</c> command line in-process, in a real temporary directory.</summary>
internal sealed class Sof : IDisposable
{
    public Sof()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("officina-cli-").FullName;
    }

    public string Directory { get; }

    public Dictionary<string, string> Variables { get; } = [];

    public Sof Write(string relativePath, string text)
    {
        File.WriteAllText(Path.Combine(Directory, relativePath), text);
        return this;
    }

    public async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await SofCommandLine.RunAsync(args, new SofConsole(output, error, Directory, Variables), TestContext.Current.CancellationToken);
        return (exitCode, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}
