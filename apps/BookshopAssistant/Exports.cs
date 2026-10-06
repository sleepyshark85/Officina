using Sleepyshark.Officina;
using Sleepyshark.Officina.Mcp;

namespace BookshopAssistant;

/// <summary>
/// The export tools (APP-12): the reference filesystem MCP server, run in Docker over stdio by the compose file's
/// <c>filesystem</c> service, which sees only the <c>exports</c> folder. The allow-list holds what an export needs:
/// writing a file, which needs approval, and listing the folder. MCP annotations are not trusted (MCP-02): every tool is a
/// write unless the host marks it read, so the application marks the listing read.
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
    /// <remarks>
    /// Ctrl+C, which cancels a reply (APP-03), reaches every process of the terminal's foreground process group, and stops
    /// <c>docker compose run</c> with its container. On Linux the server therefore runs in a session of its own
    /// (<c>setsid</c>), out of Ctrl+C's reach. Elsewhere it shares the console, so Ctrl+C also stops it, possibly during a
    /// write; the next reply starts it again (MCP-04), and the audit trail shows it as disconnected.
    /// </remarks>
    public static McpServer Server(string composeFile, string? folder = null)
    {
        string[] compose =
        [
            "docker", "compose", "--file", composeFile, "run", "--rm", "--no-TTY",
            .. folder is null ? Array.Empty<string>() : ["--volume", $"{folder}:/projects/exports"], "filesystem",
        ];
        return OperatingSystem.IsLinux()
            ? McpServer.Stdio("filesystem", "setsid", ["--wait", .. compose])
            : McpServer.Stdio("filesystem", compose[0], compose[1..]);
    }

    /// <summary>Starts the server and pins its allowed tools.</summary>
    public static Task<McpToolSource> ConnectAsync(McpServer server, CancellationToken cancellationToken) =>
        McpToolSource.ConnectAsync(server, Allowed, cancellationToken);
}
