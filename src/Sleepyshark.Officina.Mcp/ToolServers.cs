using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Mcp;

/// <summary>
/// The configured tool servers, connected, and their tools as core tools (TOOL-01). The host passes <see cref="Tools"/>
/// to the runner with the application's own tools, and the tool pipeline governs them alike (TEST-17). Each server's
/// list is read once, when connecting, so the tools offered to running agents never change (CTX-10).
/// </summary>
public sealed class ToolServers : IAsyncDisposable
{
    private readonly List<McpConnection> connections;

    private ToolServers(List<McpConnection> connections, Dictionary<string, ITool> tools)
    {
        this.connections = connections;
        Tools = tools;
    }

    /// <summary>Every server's tools, by the <c>&lt;server&gt;/&lt;tool&gt;</c> that <c>mcp:</c> sources name.</summary>
    public IReadOnlyDictionary<string, ITool> Tools { get; }

    /// <summary>Starts or reaches every server in <c>toolServers</c>, and reads its tool list.</summary>
    /// <param name="options">The configuration.</param>
    /// <param name="secrets">Where the servers' environment variables and headers are read.</param>
    /// <param name="ct">Ends the wait for a server that does not answer.</param>
    /// <exception cref="ConfigurationException">The configuration has errors.</exception>
    public static async Task<ToolServers> ConnectAsync(OfficinaOptions options, ISecretSource secrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        if (options.Validate() is { Count: > 0 } errors)
        {
            throw new ConfigurationException(errors);
        }

        var connections = new List<McpConnection>();
        var tools = new Dictionary<string, ITool>();
        try
        {
            foreach (var (name, server) in options.ToolServers)
            {
                McpConnection connection = server.Transport == ToolServerTransport.Stdio
                    ? StdioConnection.Start(server.Command!, server.Args, await ReadAsync(server.Env, secrets, ct).ConfigureAwait(false))
                    : new HttpConnection(new Uri(server.Url!), await ReadAsync(server.Headers, secrets, ct).ConfigureAwait(false));
                connections.Add(connection);
                await connection.InitializeAsync(ct).ConfigureAwait(false);
                foreach (var (tool, descriptor) in await connection.ListToolsAsync(ct).ConfigureAwait(false))
                {
                    tools[$"{name}/{tool}"] = new McpTool(connection, tool, descriptor);
                }
            }
        }
        catch
        {
            await DisposeAsync(connections).ConfigureAwait(false);
            throw;
        }

        return new ToolServers(connections, tools);
    }

    /// <summary>Stops the stdio servers and closes the connections.</summary>
    public ValueTask DisposeAsync() => DisposeAsync(connections);

    private static async ValueTask DisposeAsync(List<McpConnection> connections)
    {
        foreach (var connection in connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<string, string>> ReadAsync(IReadOnlyDictionary<string, SecretReference> references, ISecretSource secrets, CancellationToken ct)
    {
        var values = new Dictionary<string, string>();
        foreach (var (name, reference) in references)
        {
            values[name] = await secrets.GetAsync(reference.Secret, ct).ConfigureAwait(false);
        }

        return values;
    }
}
