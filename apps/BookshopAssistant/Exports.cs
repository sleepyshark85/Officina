using Sleepyshark.Officina;
using Sleepyshark.Officina.Mcp;

namespace BookshopAssistant;

/// <summary>
/// The export tools: the reference filesystem MCP server, run in Docker over stdio by the compose file's
/// <c>filesystem</c> service, which sees only the <c>exports</c> folder. The allow-list holds what an export needs:
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

    /// <summary>
    /// The server, started with <c>docker compose run</c> on <paramref name="composeFile"/>; <paramref name="folder"/>, if
    /// given, replaces the compose file's <c>exports</c> folder.
    /// </summary>
    /// <remarks>
    /// Ctrl+C, which cancels a reply, reaches the terminal's whole foreground process group and would stop <c>docker
    /// compose run</c>. On Linux the server therefore runs in its own session (<c>setsid</c>). Elsewhere Ctrl+C also stops it,
    /// possibly mid-write; the next reply restarts it, and the audit trail shows it as disconnected.
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
