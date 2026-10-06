using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Mcp;

/// <summary>
/// A connection to one MCP server: JSON-RPC 2.0 over a transport, with only what a tool source needs: <c>initialize</c>,
/// <c>ping</c>, <c>tools/list</c> and <c>tools/call</c>. A transport failure loses the connection for good; the source
/// connects anew at the next run.
/// </summary>
internal abstract class McpConnection(McpServer server, Action<string> lost) : IAsyncDisposable
{
    /// <summary>The protocol versions this client speaks, newest first; it asks for the first.</summary>
    private static readonly string[] Versions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private int lastId;
    private string? lostReason;

    /// <summary>Why the connection was lost; null while it works.</summary>
    public string? Lost => Volatile.Read(ref lostReason);

    protected McpServer Server => server;

    /// <summary>The protocol version agreed with the server.</summary>
    protected string Version { get; private set; } = Versions[0];

    /// <summary>Set while the source closes the connection, so its end is not reported as a loss.</summary>
    protected bool Closing { get; set; }

    /// <summary>Agrees on the protocol; the server accepts other requests only after this.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = Version,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "officina", ["version"] = "1.0.0" },
        }, cancellationToken).ConfigureAwait(false);
        var version = result.TryGetProperty("protocolVersion", out var given) ? given.GetString() : null;
        Version = Versions.Contains(version)
            ? version!
            : throw new InvalidOperationException($"The MCP server '{server.Name}' speaks protocol {version}; this client speaks {string.Join(", ", Versions)}.");
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks that the server answers; an error answer counts, as it came from the server.</summary>
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RequestAsync("ping", null, cancellationToken).ConfigureAwait(false);
        }
        catch (McpErrorException)
        {
        }
    }

    /// <summary>The server's tools, from every page of its list.</summary>
    public async Task<List<JsonElement>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var result = await RequestAsync("tools/list", cursor is null ? null : new JsonObject { ["cursor"] = cursor }, cancellationToken).ConfigureAwait(false);
            tools.AddRange(result.GetProperty("tools").EnumerateArray());
            cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null);
        return tools;
    }

    /// <summary>Calls a tool; the result holds its <c>content</c> and whether it <c>isError</c>.</summary>
    public Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken) =>
        RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(arguments.GetRawText()) }, cancellationToken);

    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Sends a message: returns the server's response for a request (<paramref name="id"/> given), null for a notification.
    /// Throws <see cref="IOException"/> or <see cref="HttpRequestException"/> when the transport fails.
    /// </summary>
    protected abstract Task<JsonElement?> SendAsync(JsonObject message, int? id, CancellationToken cancellationToken);

    /// <summary>Whether a message from the server responds to a request, rather than being its own request or notification.</summary>
    protected static bool IsResponse(JsonElement message, out int id)
    {
        id = 0;
        return message.ValueKind == JsonValueKind.Object && !message.TryGetProperty("method", out _)
            && message.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out id);
    }

    /// <summary>Marks the connection lost, once, and tells the source; returns the error to throw.</summary>
    protected IOException Lose(string reason)
    {
        reason = Redact(reason);
        if (Interlocked.CompareExchange(ref lostReason, reason, null) is null && !Closing)
        {
            lost(reason);
        }

        return new IOException(Lost ?? reason);
    }

    /// <summary><paramref name="text"/> without the server's credentials, for messages quoting the server or transport.</summary>
    public string Redact(string text) =>
        server.Secrets.Where(secret => secret.Length > 0).Aggregate(text, (redacted, secret) => redacted.Replace(secret, "[redacted]", StringComparison.Ordinal));

    private async Task<JsonElement> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        if (Lost is { } reason)
        {
            throw new IOException(reason);
        }

        var id = Interlocked.Increment(ref lastId);
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        JsonElement response;
        try
        {
            response = (await SendAsync(request, id, cancellationToken).ConfigureAwait(false))!.Value;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            throw Lose($"The MCP server '{server.Name}' could not be reached: {exception.Message}");
        }

        if (response.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var text) ? text.GetString() : error.GetRawText();
            throw new McpErrorException(Redact($"The MCP server '{server.Name}' answered {method} with an error: {message}"));
        }

        return response.GetProperty("result");
    }
}

/// <summary>The server answered with a JSON-RPC error: it is reachable, but refused or failed the request.</summary>
internal sealed class McpErrorException(string message) : Exception(message);
