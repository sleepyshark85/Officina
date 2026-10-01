using System.Diagnostics;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Mcp.Tests;

/// <summary>
/// The reference tool server, configured as <c>toolServers.ref</c> for one transport. For HTTP it is started here and
/// stopped on dispose; for stdio the client starts it.
/// </summary>
internal sealed class ReferenceServer : IAsyncDisposable
{
    public const string Name = "ref";

    private static readonly string Host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    private static readonly string Program = Path.Combine(AppContext.BaseDirectory, "Sleepyshark.Officina.Mcp.TestServer.dll");

    private readonly Process? http;

    private ReferenceServer(ToolServerOptions options, Process? http)
    {
        Options = options;
        this.http = http;
    }

    public ToolServerOptions Options { get; }

    /// <summary>The secrets the server's credential is read from: right unless <paramref name="credential"/> says otherwise.</summary>
    public static InMemorySecretSource Secrets(string credential = "s3cret") =>
        new(new Dictionary<string, string> { ["TOKEN"] = credential, ["AUTH"] = $"Bearer {credential}" });

    public static async Task<ReferenceServer> StartAsync(ToolServerTransport transport)
    {
        if (transport == ToolServerTransport.Stdio)
        {
            return new(new() { Command = Host, Args = [Program], Env = new Dictionary<string, SecretReference> { ["MCP_TEST_TOKEN"] = new("TOKEN") } }, null);
        }

        var start = new ProcessStartInfo(Host) { ArgumentList = { Program, "http" }, RedirectStandardOutput = true };
        var process = Process.Start(start)!;
        var url = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken);
        return new(
            new() { Transport = ToolServerTransport.Http, Url = url, Headers = new Dictionary<string, SecretReference> { ["Authorization"] = new("AUTH") } },
            process);
    }

    /// <summary>A configuration with the server, whose agent <c>dev</c> is offered these tools.</summary>
    public OfficinaOptions With(params (string Name, ToolOptions Tool)[] tools) => new()
    {
        ToolServers = new Dictionary<string, ToolServerOptions> { [Name] = Options },
        Agents = new Dictionary<string, AgentDefinition> { ["dev"] = new() { Instructions = "Work.", Tools = ["all"] } },
        Tools = tools.ToDictionary(tool => tool.Name, tool => tool.Tool),
        ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["all"] = [.. tools.Select(tool => tool.Name)] },
    };

    public async ValueTask DisposeAsync()
    {
        if (http is not null)
        {
            http.Kill();
            await http.WaitForExitAsync();
            http.Dispose();
        }
    }
}
