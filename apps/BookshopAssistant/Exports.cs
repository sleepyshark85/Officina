using Sleepyshark.Officina;
using Sleepyshark.Officina.Mcp;

namespace BookshopAssistant;

/// <summary>
/// The export tools (APP-12): the reference filesystem MCP server, run in Docker over stdio by the compose file's
/// <c>filesystem</c> service, which sees only the <c>exports</c> folder. The allow-list holds what an export needs:
/// writing a file, which needs approval, and listing the folder.
/// </summary>
public static class Exports
{
    /// <summary>The server's tools the agent gets, by their name on the server.</summary>
    public static IReadOnlyList<AllowedTool> Allowed { get; } =
    [
        new("write_file", ToolKind.Write, NeedsApproval: true),
        new("list_directory", ToolKind.Read),
    ];

    /// <summary>
    /// The server, started with <c>docker compose run</c> on <paramref name="composeFile"/>; <paramref name="folder"/>,
    /// if given, replaces the compose file's <c>exports</c> folder.
    /// </summary>
    public static McpServer Server(string composeFile, string? folder = null) => McpServer.Stdio(
        "filesystem",
        "docker",
        ["compose", "--file", composeFile, "run", "--rm", "--no-TTY", .. folder is null ? Array.Empty<string>() : ["--volume", $"{folder}:/projects/exports"], "filesystem"]);

    /// <summary>Starts the server and pins its allowed tools.</summary>
    public static Task<McpToolSource> ConnectAsync(McpServer server, CancellationToken cancellationToken) =>
        McpToolSource.ConnectAsync(server, Allowed, cancellationToken);
}
