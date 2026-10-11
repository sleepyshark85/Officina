using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>The server's name, which prefixes its tools and keys its source in the container.</summary>
    public const string Name = "filesystem";

    /// <summary>Connects to the server at <paramref name="url"/>, its <c>/mcp</c> endpoint, pins its allowed tools and registers it.</summary>
    public static Task<IServiceCollection> AddExportsAsync(this IServiceCollection services, Uri url, CancellationToken cancellationToken) =>
        services.AddMcpToolSourceAsync(McpServer.Http(Name, url), Allowed, cancellationToken);
}
