using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Mcp;

/// <summary>
/// A connection to one MCP tool server: JSON-RPC 2.0 requests and their responses over a transport. It implements only
/// what the core uses: <c>initialize</c>, <c>tools/list</c> and <c>tools/call</c>.
/// </summary>
internal abstract class McpConnection : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";

    private int lastId;

    /// <summary>Agrees on the protocol with the server; it accepts other requests only after this.</summary>
    /// <exception cref="InvalidOperationException">The server answers with a protocol version this client does not speak.</exception>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "officina", ["version"] = CoreVersion.Value },
        }, ct).ConfigureAwait(false);
        if (result.GetProperty("protocolVersion").GetString() is var version && version != ProtocolVersion)
        {
            throw new InvalidOperationException($"The tool server speaks protocol {version}; this client speaks {ProtocolVersion}.");
        }

        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, null, ct).ConfigureAwait(false);
    }

    /// <summary>The server's tools, from every page of its list.</summary>
    public async Task<IReadOnlyList<(string Name, ToolDescriptor Descriptor)>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<(string, ToolDescriptor)>();
        string? cursor = null;
        do
        {
            var result = await RequestAsync("tools/list", cursor is null ? null : new JsonObject { ["cursor"] = cursor }, ct).ConfigureAwait(false);
            foreach (var tool in result.GetProperty("tools").EnumerateArray())
            {
                // A server's annotations are hints only, so its tools declare nothing: the catalog treats them as writes
                // and as unsafe to run in parallel unless configured otherwise.
                var description = tool.TryGetProperty("description", out var text) ? text.GetString() ?? "" : "";
                tools.Add((tool.GetProperty("name").GetString()!, new ToolDescriptor(description, tool.GetProperty("inputSchema").Clone(), ToolKind.Read)));
            }

            cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null);
        return tools;
    }

    /// <summary>Calls a tool; the result holds its <c>content</c> and whether it <c>isError</c>.</summary>
    public Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken ct) =>
        RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(arguments.GetRawText()) }, ct);

    public abstract ValueTask DisposeAsync();

    /// <summary>Sends a message. For a request, returns the response with its id; for a notification, null.</summary>
    protected abstract Task<JsonElement?> SendAsync(JsonObject message, int? id, CancellationToken ct);

    /// <summary>Whether a message from the server is the response to the request with this id, rather than a notification or another response.</summary>
    protected static bool IsResponse(JsonElement message, int id) =>
        message.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id && !message.TryGetProperty("method", out _);

    private async Task<JsonElement> RequestAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref lastId);
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        var response = (await SendAsync(request, id, ct).ConfigureAwait(false))!.Value;

        // The tool pipeline logs this, and tells the model only that the call failed (TOOL-08).
        return response.TryGetProperty("error", out var error)
            ? throw new InvalidOperationException($"The tool server answered {method} with an error: {error.GetRawText()}")
            : response.GetProperty("result");
    }
}
