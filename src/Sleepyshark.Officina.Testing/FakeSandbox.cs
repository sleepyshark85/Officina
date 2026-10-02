using System.Collections.Concurrent;
using System.Threading.Channels;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// A sandbox that runs nothing (DESIGN.md §11). It remembers each command and answers with output scripted in advance,
/// in order.
/// </summary>
public sealed class FakeSandbox : ISandbox
{
    private readonly ConcurrentQueue<(string Output, int? ExitCode)> replies = new();
    private readonly ConcurrentQueue<FakeSandboxProcess> processes = new();
    private readonly ConcurrentQueue<string> released = new();
    private readonly TaskCompletionSource<FakeSandboxProcess> firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What <see cref="Probe"/> reports: null when the machine can isolate commands.</summary>
    public string? Problem { get; init; }

    /// <summary>The processes started so far, in order.</summary>
    public IReadOnlyList<FakeSandboxProcess> Processes => [.. processes];

    /// <summary>The first process, once it has started.</summary>
    public Task<FakeSandboxProcess> FirstStarted => firstStarted.Task;

    /// <summary>Adds a reply to the end of the script.</summary>
    /// <param name="output">The command's output.</param>
    /// <param name="exitCode">Its exit code, or null for a command that runs until it is stopped.</param>
    public FakeSandbox Reply(string output, int? exitCode = 0)
    {
        ArgumentNullException.ThrowIfNull(output);
        replies.Enqueue((output, exitCode));
        return this;
    }

    /// <summary>The working copies released so far, in order.</summary>
    public IReadOnlyList<string> Released => [.. released];

    public string? Probe() => Problem;

    public void Release(string directory, IReadOnlyList<string> toolchains) => released.Enqueue(directory);

    public ValueTask<ISandboxProcess> StartAsync(SandboxCommand command, CancellationToken ct)
    {
        if (!replies.TryDequeue(out var reply))
        {
            throw new InvalidOperationException($"The fake sandbox was asked to run \"{command.CommandLine}\" but has no reply left. Add one with Reply().");
        }

        var process = new FakeSandboxProcess(command, reply.Output, reply.ExitCode);
        processes.Enqueue(process);
        firstStarted.TrySetResult(process);
        return ValueTask.FromResult<ISandboxProcess>(process);
    }
}

/// <summary>A command the fake sandbox was given, with its scripted output.</summary>
public sealed class FakeSandboxProcess : ISandboxProcess
{
    private readonly Channel<string> output = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal FakeSandboxProcess(SandboxCommand command, string text, int? exitCode)
    {
        Command = command;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            output.Writer.TryWrite(line);
        }

        if (exitCode is { } code)
        {
            End(code);
        }
    }

    public SandboxCommand Command { get; }

    /// <summary>Whether it was stopped while it still ran.</summary>
    public bool Stopped { get; private set; }

    public ChannelReader<string> Output => output.Reader;

    public Task<int> ExitCode => exit.Task;

    public ValueTask DisposeAsync()
    {
        if (!exit.Task.IsCompleted)
        {
            Stopped = true;
            End(-1);
        }

        return ValueTask.CompletedTask;
    }

    private void End(int code)
    {
        output.Writer.TryComplete();
        exit.TrySetResult(code);
    }
}
