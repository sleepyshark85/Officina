using Sleepyshark.Officina;
using Sleepyshark.Officina.Mcp;

namespace BookshopAssistant;

/// <summary>
/// The export tools: the reference filesystem MCP server, which the compose file's <c>filesystem</c> service runs in
/// Docker over Streamable HTTP and which sees only the <c>exports</c> folder. The allow-list holds what an export needs:
/// writing a file (with approval) and listing the folder, which is marked read as MCP tools are writes by default.
/// </summary>
public static class Exports
{
    /// <summary>The server's tools the agent gets, by their name on the server.</summary>
    public static IReadOnlyList<AllowedTool> Allowed { get; } =
    [
        new("write_file", ToolKind.Write, NeedsApproval: true),
        new("list_directory", ToolKind.Read),
    ];

    /// <summary>The server at <paramref name="url"/>, its <c>/mcp</c> endpoint.</summary>
    public static McpServer Server(Uri url) => McpServer.Http("filesystem", url);

    /// <summary>Connects to the server and pins its allowed tools.</summary>
    public static Task<McpToolSource> ConnectAsync(McpServer server, CancellationToken cancellationToken) =>
        McpToolSource.ConnectAsync(server, Allowed, cancellationToken);
}
