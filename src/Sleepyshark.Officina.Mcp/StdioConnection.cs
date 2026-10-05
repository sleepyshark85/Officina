using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Mcp;

/// <summary>
/// The stdio transport: the server is a child process, and each message is one line of its standard input or output.
/// A reader matches responses to requests by id, so calls may be in flight together; when the process's output ends,
/// the connection is lost and every waiting request fails.
/// </summary>
internal sealed class StdioConnection : McpConnection
{
    /// <summary>How long a closing server may take to exit, once its input is closed, before it is killed.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    private readonly Process process;
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> waiting = new();
    private readonly Task reading;
    private volatile string? lastError;

    private StdioConnection(McpServer server, Action<string> lost, Process process)
        : base(server, lost)
    {
        this.process = process;

        // The server's log is not kept, but it is drained, so a server that logs a lot never blocks; its last line
        // explains a server that exits.
        process.ErrorDataReceived += (_, line) => lastError = string.IsNullOrWhiteSpace(line.Data) ? lastError : line.Data;
        process.BeginErrorReadLine();
        reading = Task.Run(ReadAsync);
    }

    public static StdioConnection Start(McpServer server, Action<string> lost)
    {
        var start = new ProcessStartInfo(server.Command!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in server.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in server.Environment)
        {
            start.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new IOException($"The MCP server '{server.Name}' could not be started ({server.Command}): {exception.Message}", exception);
        }

        return new StdioConnection(server, lost, process);
    }

    public override async ValueTask DisposeAsync()
    {
        Closing = true;
        try
        {
            // Closing its input lets the server exit by itself, which a container needs to be removed.
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(ExitWait).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception killing) when (killing is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // It exited meanwhile.
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
        }

        await reading.ConfigureAwait(false);
        process.Dispose();
        writing.Dispose();
    }

    protected override async Task<JsonElement?> SendAsync(JsonObject message, int? id, CancellationToken cancellationToken)
    {
        var response = id is { } key ? waiting.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)) : null;
        try
        {
            // The reader fails the requests waiting when it stops; one added after that sees the loss here.
            if (Lost is { } reason)
            {
                throw new IOException(reason);
            }

            await writing.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteAsync((message.ToJsonString() + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writing.Release();
            }

            return response is null ? null : await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (id is { } done)
            {
                waiting.TryRemove(done, out _);
            }
        }
    }

    /// <summary>Hands each response to the request waiting for it; skips the server's own requests, notifications and other output.</summary>
    private async Task ReadAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                JsonElement message;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    message = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                if (IsResponse(message, out var id) && waiting.TryGetValue(id, out var response))
                {
                    response.TrySetResult(message);
                }
            }
        }
        catch (IOException)
        {
        }

        // The server's last words on its error output often say why it ended; they are read in full once it exits.
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        var error = Lose($"The MCP server '{Server.Name}' closed its connection{(lastError is { } said ? $": {said}" : ".")}");
        foreach (var response in waiting.Values)
        {
            response.TrySetException(error);
        }
    }
}
