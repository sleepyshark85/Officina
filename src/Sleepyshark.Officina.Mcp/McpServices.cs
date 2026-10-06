using Microsoft.Extensions.DependencyInjection;

namespace Sleepyshark.Officina.Mcp;

/// <summary>Registers MCP tool sources.</summary>
public static class McpServices
{
    /// <summary>
    /// Connects to <paramref name="server"/> and registers the source under the server's name. Connecting is asynchronous,
    /// which a container cannot do when it makes a service, so it happens here; the container still owns the source and
    /// disposes it. Throws as <see cref="McpToolSource.ConnectAsync(McpServer, IEnumerable{AllowedTool}, CancellationToken)"/> does, registering nothing.
    /// </summary>
    public static async Task<IServiceCollection> AddMcpToolSourceAsync(
        this IServiceCollection services, McpServer server, IEnumerable<AllowedTool> allowed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var source = await McpToolSource.ConnectAsync(server, allowed, cancellationToken).ConfigureAwait(false);
        return services.AddKeyedSingleton(server.Name, (_, _) => source);
    }
}
