using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// Runs the real <c>sof</c> command line in-process, in a real temporary directory. The owner at the console, the signals
/// they send, the model providers and the clock are stand-ins.
/// </summary>
internal sealed class Sof : IDisposable
{
    private readonly List<Owner> consoles = [];
    private Action<PosixSignal>? signalled;

    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("officina-cli-").FullName;

    public Dictionary<string, string> Variables { get; } = [];

    public Dictionary<string, IModelProvider> Providers { get; } = [];

    public FakeSandbox? Sandbox { get; set; }

    public FakeTimeProvider Time { get; } = new();

    /// <summary>Stands in for Ctrl+C: cancels the command as the process termination signal does.</summary>
    public CancellationToken Cancel { get; set; }

    /// <summary>Standard output, which a test can wait on.</summary>
    public Console Out { get; } = new();

    /// <summary>What the owner types; nothing by default.</summary>
    public Owner In { get; private set; } = new();

    /// <summary>
    /// A new console for the next command. A command that read the old one may have left a read waiting on it, as a process
    /// leaves its console read to end with it, and that read would take the next line.
    /// </summary>
    public void NewConsole()
    {
        consoles.Add(In);
        In = new();
    }

    /// <summary>Stands in for a signal from the terminal, such as Ctrl+C, to a command that takes signals over (<c>sof chat</c>).</summary>
    public void Press(PosixSignal signal) => (signalled ?? throw new InvalidOperationException("No command takes signals now.")).Invoke(signal);

    public Sof Write(string relativePath, string text)
    {
        File.WriteAllText(Path.Combine(Directory, relativePath), text);
        return this;
    }

    public async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        using var error = new StringWriter();
        var start = Out.ToString().Length;
        var host = new SofEnvironment(Out, error, Directory, Variables)
        {
            In = In, Providers = Providers, Sandbox = Sandbox, Time = Time,
            Signals = handler =>
            {
                signalled = handler;
                return new Registration(() => signalled = null);
            },
        };
        var exitCode = await SofCommandLine.RunAsync(args, host, Cancel);
        return (exitCode, Out.ToString()[start..].ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    public void Dispose()
    {
        Out.Dispose();
        In.Dispose();
        consoles.ForEach(console => console.Dispose());

        // Git makes its object files read-only, which stops Windows deleting them.
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        System.IO.Directory.Delete(Directory, recursive: true);
    }

    /// <summary>Makes the directory a real git repository with <c>main</c> checked out and everything in it committed.</summary>
    public Sof Commit()
    {
        foreach (var arguments in new[] { "init --initial-branch=main", "config user.name owner", "config user.email owner@example.com", "add --all", "commit -m Start" })
        {
            using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", arguments) { WorkingDirectory = Directory, RedirectStandardOutput = true })!;
            git.StandardOutput.ReadToEnd();
            git.WaitForExit();
        }

        return this;
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// <summary>How long output may stay unchanged before a wait for it fails: far longer than any step of a test takes.</summary>
    internal static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);

    /// <summary>The command's result, or a failure with its output if it has not ended within <see cref="Quiet"/>.</summary>
    public async Task<(int ExitCode, string Output, string Error)> EndedAsync(Task<(int ExitCode, string Output, string Error)> command)
    {
        try
        {
            return await command.WaitAsync(Quiet);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The command did not end within {Quiet}. The output:\n{Out}");
        }
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

                try
                {
                    await next.WaitAsync(Quiet, ct);
                }
                catch (TimeoutException)
                {
                    // A hang fails the test with what it shows, rather than holding the test run until its own limit.
                    throw new TimeoutException($"Nothing was written for {Quiet} while waiting for \"{text}\". The output:\n{ToString()}");
                }
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

    /// <summary>
    /// The owner typing commands, one line at a time, as the test decides. Behaves like <c>Console.In</c>: its <c>ReadLineAsync</c> blocks the calling thread until a line is typed, and
    /// ignores cancellation. It returns null once disposed.
    /// </summary>
    internal sealed class Owner : TextReader
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();

        public void Type(string line) => lines.Writer.TryWrite(line);

        public override string? ReadLine() =>
            lines.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult() && lines.Reader.TryRead(out var line) ? line : null;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult(ReadLine());

        protected override void Dispose(bool disposing)
        {
            lines.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
