using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Mcp;

/// <summary>The stdio transport: the server is a child process, and each message is one line of its standard input or output.</summary>
internal sealed class StdioConnection : McpConnection
{
    private readonly Process process;
    private readonly SemaphoreSlim oneAtATime = new(1, 1);

    private StdioConnection(Process process) => this.process = process;

    /// <param name="command">The program that runs the server.</param>
    /// <param name="args">Its arguments.</param>
    /// <param name="environment">Environment variables to set for it.</param>
    public static StdioConnection Start(string command, IEnumerable<string> args, IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"The tool server {command} did not start.");

        // The server's log is not read, but it is drained, so a server that logs a lot never blocks.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return new StdioConnection(process);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }

        process.Dispose();
        oneAtATime.Dispose();
    }

    protected override async Task<JsonElement?> SendAsync(JsonObject message, int? id, CancellationToken ct)
    {
        await oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteAsync((message.ToJsonString() + "\n").AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            while (id is not null)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new IOException("The tool server closed its output.");
                using var document = JsonDocument.Parse(line);

                // Notifications, and responses to requests that were cancelled, are skipped.
                if (IsResponse(document.RootElement, id.Value))
                {
                    return document.RootElement.Clone();
                }
            }

            return null;
        }
        finally
        {
            oneAtATime.Release();
        }
    }
}
