using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;

namespace Sleepyshark.Officina.Mcp;

/// <summary>
/// An MCP server's tools, as core tools (ARCHITECTURE §7). It reads the server's tool list once, when it connects, keeps
/// only the allowed tools, names each <c>&lt;server&gt;__&lt;tool&gt;</c> and pins them: <see cref="Tools"/> never
/// changes, so they stay the same for every conversation of an agent built with them (MCP-03, CTX-04). The tool pipeline
/// runs their calls like any other tool's (MCP-02). Each run reconnects a server that was lost, and fails if it cannot;
/// a server lost during a run gives error results for its calls (MCP-04).
/// </summary>
public sealed class McpToolSource : IToolSource, IAsyncDisposable
{
    /// <summary>How long connecting, or checking a connection, may take.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly McpServer server;
    private readonly SemaphoreSlim connecting = new(1, 1);
    private readonly ConcurrentQueue<ToolSourceChange> changes = new();
    private volatile McpConnection? connection;

    private McpToolSource(McpServer server) => this.server = server;

    public string Name => server.Name;

    /// <summary>The allowed tools, in the order the allow-list gives them.</summary>
    public ImmutableArray<Tool> Tools { get; private set; } = [];

    /// <summary>Connects to <paramref name="server"/> and pins its <paramref name="allowed"/> tools.</summary>
    /// <exception cref="IOException">The server cannot be started or reached.</exception>
    /// <exception cref="InvalidOperationException">The server lacks an allowed tool, or refuses the protocol.</exception>
    public static async Task<McpToolSource> ConnectAsync(McpServer server, IEnumerable<AllowedTool> allowed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(allowed);
        var source = new McpToolSource(server);
        try
        {
            await source.ConnectAsync(cancellationToken).ConfigureAwait(false);
            using var timeout = Timeout(cancellationToken);
            var listed = (await source.connection!.ListToolsAsync(timeout.Token).ConfigureAwait(false))
                .ToDictionary(tool => tool.GetProperty("name").GetString()!);
            source.Tools = [.. allowed.Select(tool => source.Pin(tool, listed))];
            return source;
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Checks the connection, and connects anew if it was lost; the tool list stays pinned.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = Timeout(cancellationToken);
            if (connection is { Lost: null } current)
            {
                try
                {
                    await current.PingAsync(timeout.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    changes.Enqueue(new(ToolSourceState.Disconnected, $"The MCP server '{Name}' did not answer within {ConnectTimeout.TotalSeconds:0} s."));
                }
                catch (IOException)
                {
                    // The loss is already reported.
                }
            }

            if (connection is { } old)
            {
                connection = null;
                await old.DisposeAsync().ConfigureAwait(false);
            }

            McpConnection? opened = null;
            try
            {
                // A connection that fails while connecting is reported as failed, not also as lost.
                opened = server.Url is null ? StdioConnection.Start(server, Lost) : new HttpConnection(server, Lost);

                void Lost(string reason)
                {
                    if (connection is { } current && current == opened)
                    {
                        changes.Enqueue(new(ToolSourceState.Disconnected, reason));
                    }
                }

                await opened.InitializeAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Closed on any failure, cancellation included, so no server process outlives a connect.
                if (opened is not null)
                {
                    await opened.DisposeAsync().ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                var reason = exception is OperationCanceledException ? $"The MCP server '{Name}' did not answer within {ConnectTimeout.TotalSeconds:0} s." : exception.Message;
                changes.Enqueue(new(ToolSourceState.Failed, reason));
                throw new IOException(reason, exception);
            }

            connection = opened;
            changes.Enqueue(new(ToolSourceState.Connected));
        }
        finally
        {
            connecting.Release();
        }
    }

    public IReadOnlyList<ToolSourceChange> TakeChanges()
    {
        var taken = new List<ToolSourceChange>();
        while (changes.TryDequeue(out var change))
        {
            taken.Add(change);
        }

        return taken;
    }

    /// <summary>Closes the connection; a stdio server is asked to exit, and stopped if it does not.</summary>
    public async ValueTask DisposeAsync()
    {
        if (connection is { } current)
        {
            connection = null;
            await current.DisposeAsync().ConfigureAwait(false);
        }

        connecting.Dispose();
    }

    private static CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        return timeout;
    }

    /// <summary>The core tool for an allowed tool of the server's list.</summary>
    private Tool Pin(AllowedTool allowed, Dictionary<string, JsonElement> listed)
    {
        if (!listed.TryGetValue(allowed.Name, out var tool))
        {
            throw new InvalidOperationException($"The MCP server '{Name}' has no tool '{allowed.Name}'. It has: {string.Join(", ", listed.Keys.Order(StringComparer.Ordinal))}.");
        }

        var readOnly = tool.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Object
            && annotations.TryGetProperty("readOnlyHint", out var hint) && hint.ValueKind == JsonValueKind.True;
        var description = tool.TryGetProperty("description", out var text) ? text.GetString() ?? "" : "";
        try
        {
            return new Tool(
                $"{Name}__{allowed.Name}", description, tool.GetProperty("inputSchema").GetRawText(), allowed.Kind ?? (readOnly ? ToolKind.Read : ToolKind.Write),
                (input, cancellationToken) => CallAsync(allowed.Name, input, cancellationToken), allowed.NeedsApproval)
            { Source = this };
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"The tool '{allowed.Name}' of the MCP server '{Name}' cannot be used: {exception.Message}", exception);
        }
    }

    /// <summary>Calls a tool on the server; a server that is not connected, or fails, throws, which gives the model an error result.</summary>
    private async Task<ToolOutput> CallAsync(string name, JsonElement input, CancellationToken cancellationToken)
    {
        var current = connection;
        if (current is null || current.Lost is not null)
        {
            throw new IOException($"The MCP server '{Name}' is not connected{(current?.Lost is { } reason ? $": {reason}" : ".")}");
        }

        // The server's output is redacted of its own credentials here, whether or not the host added them to the agent's secrets (EVT-03).
        var result = await current.CallToolAsync(name, input, cancellationToken).ConfigureAwait(false);
        var content = result.TryGetProperty("content", out var items) && items.ValueKind == JsonValueKind.Array
            ? string.Join("\n", items.EnumerateArray().Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : $"[{item.GetProperty("type").GetString()} content]"))
            : "";
        return new ToolOutput(current.Redact(content), result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True);
    }
}
