using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// Runs the real <c>sof</c> command line in-process, in a real temporary directory. The owner at the console, the model
/// providers and the clock are stand-ins.
/// </summary>
internal sealed class Sof : IDisposable
{
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("officina-cli-").FullName;

    public Dictionary<string, string> Variables { get; } = [];

    public Dictionary<string, IModelProvider> Providers { get; } = [];

    public FakeTimeProvider Time { get; } = new();

    /// <summary>Standard output, which a test can wait on.</summary>
    public Console Out { get; } = new();

    /// <summary>What the owner types; nothing by default.</summary>
    public Owner In { get; } = new();

    public Sof Write(string relativePath, string text)
    {
        File.WriteAllText(Path.Combine(Directory, relativePath), text);
        return this;
    }

    public async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        using var error = new StringWriter();
        var start = Out.ToString().Length;
        var host = new SofEnvironment(Out, error, Directory, Variables) { In = In, Providers = Providers, Time = Time };
        var exitCode = await SofCommandLine.RunAsync(args, host);
        return (exitCode, Out.ToString()[start..].ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    public void Dispose()
    {
        Out.Dispose();
        In.Dispose();
        System.IO.Directory.Delete(Directory, recursive: true);
    }

    /// <summary>Output that a test can wait on until it shows some text.</summary>
    internal sealed class Console : StringWriter
    {
        private readonly Lock gate = new();
        private TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Write(char value)
        {
            lock (gate)
            {
                base.Write(value);
                Interlocked.Exchange(ref written, new(TaskCreationOptions.RunContinuationsAsynchronously)).SetResult();
            }
        }

        public override void Write(string? value)
        {
            lock (gate)
            {
                base.Write(value);
                Interlocked.Exchange(ref written, new(TaskCreationOptions.RunContinuationsAsynchronously)).SetResult();
            }
        }

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        public override void WriteLine(string? value) => Write(value + NewLine);

        public async Task WaitForAsync(string text, CancellationToken ct)
        {
            while (true)
            {
                Task next;
                lock (gate)
                {
                    if (ToString().Contains(text, StringComparison.Ordinal))
                    {
                        return;
                    }

                    next = written.Task;
                }

                await next.WaitAsync(ct);
            }
        }

        public override string ToString()
        {
            lock (gate)
            {
                return base.ToString();
            }
        }
    }

    /// <summary>The owner typing commands, one line at a time, as the test decides.</summary>
    internal sealed class Owner : TextReader
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();

        public void Type(string line) => lines.Writer.TryWrite(line);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await lines.Reader.WaitToReadAsync(cancellationToken) && lines.Reader.TryRead(out var line) ? line : null;
    }
}
