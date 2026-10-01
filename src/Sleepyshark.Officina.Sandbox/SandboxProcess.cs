using System.Threading.Channels;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Sandbox;

/// <summary>
/// A sandboxed command, started by an operating system's sandbox. It reads the command's output, keeps it within its
/// limit (SBX-01), and when the command ends, stops whatever is left of it and the proxy.
/// </summary>
internal sealed class SandboxProcess : ISandboxProcess
{
    private readonly Action stop;
    private readonly FilterProxy? proxy;
    private readonly Channel<string> output = Channel.CreateUnbounded<string>();
    private readonly int limit;
    private int kept;

    /// <param name="exited">The exit code of the command's first process, once it ends.</param>
    /// <param name="outputs">The command's output. Everything it started holds it open, so the command has ended when it ends.</param>
    /// <param name="stop">Stops the command and everything it started; called again when the command has ended.</param>
    /// <param name="outputCharacters">How much output is kept.</param>
    /// <param name="proxy">The command's filtering proxy, if it may reach any host.</param>
    public SandboxProcess(Task<int> exited, IReadOnlyList<StreamReader> outputs, Action stop, int outputCharacters, FilterProxy? proxy)
    {
        this.stop = stop;
        this.proxy = proxy;
        limit = outputCharacters;
        ExitCode = WaitAsync(exited, outputs);
    }

    public ChannelReader<string> Output => output.Reader;

    public Task<int> ExitCode { get; }

    public async ValueTask DisposeAsync()
    {
        stop();
        await ExitCode.ConfigureAwait(false);
    }

    private async Task<int> WaitAsync(Task<int> exited, IReadOnlyList<StreamReader> outputs)
    {
        await Task.WhenAll(outputs.Select(ReadAsync)).ConfigureAwait(false);
        var exitCode = await exited.ConfigureAwait(false);
        stop();
        output.Writer.Complete();
        if (proxy is not null)
        {
            await proxy.DisposeAsync().ConfigureAwait(false);
        }

        return exitCode;
    }

    private async Task ReadAsync(StreamReader reader)
    {
        using (reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Add(line);
            }
        }
    }

    /// <summary>Adds a line of the sandbox's own to the output, such as a decision of the proxy.</summary>
    public void Add(string line)
    {
        lock (output)
        {
            if (kept > limit)
            {
                return;
            }

            kept += line.Length + 1;
            output.Writer.TryWrite(kept <= limit ? line : $"[Output stopped: the command wrote more than {limit} characters.]");
        }
    }
}
