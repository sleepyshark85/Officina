using System.Diagnostics;
using System.Threading.Channels;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Sandbox;

/// <summary>
/// A sandboxed command, started by the operating system's sandbox. The OS sandbox makes killing the process it starts
/// end everything inside, and this class keeps the output within its limit (SBX-01) and ends the proxy with the command.
/// </summary>
internal sealed class SandboxProcess : ISandboxProcess
{
    private readonly Process process;
    private readonly FilterProxy? proxy;
    private readonly Channel<string> output = Channel.CreateUnbounded<string>();
    private readonly int limit;
    private int kept;

    public SandboxProcess(ProcessStartInfo start, int outputCharacters, FilterProxy? proxy)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        limit = outputCharacters;
        this.proxy = proxy;
        process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, line) => Add(line.Data);
        process.ErrorDataReceived += (_, line) => Add(line.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        ExitCode = WaitAsync();
    }

    public ChannelReader<string> Output => output.Reader;

    public Task<int> ExitCode { get; }

    public async ValueTask DisposeAsync()
    {
        process.Kill(entireProcessTree: true);
        await ExitCode.ConfigureAwait(false);
        process.Dispose();
    }

    private async Task<int> WaitAsync()
    {
        // This also waits for the output to end, which happens only when nothing inside holds it open any more.
        await process.WaitForExitAsync().ConfigureAwait(false);
        output.Writer.Complete();
        if (proxy is not null)
        {
            await proxy.DisposeAsync().ConfigureAwait(false);
        }

        return process.ExitCode;
    }

    private void Add(string? line)
    {
        lock (output)
        {
            if (line is null || kept > limit)
            {
                return;
            }

            kept += line.Length + 1;
            output.Writer.TryWrite(kept <= limit ? line : $"[Output stopped: the command wrote more than {limit} characters.]");
        }
    }
}
