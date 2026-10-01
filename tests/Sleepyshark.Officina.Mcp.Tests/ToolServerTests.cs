using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Mcp.Tests;

/// <summary>Our own MCP client against the reference server, over both transports (TOOL-01).</summary>
public class ToolServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ToolServerTransport.Stdio)]
    [InlineData(ToolServerTransport.Http)]
    public async Task A_servers_tools_are_offered_sorted_by_name_and_run_on_the_server(ToolServerTransport transport)
    {
        await using var server = await ReferenceServer.StartAsync(transport);
        var options = server.With(
            ("upper", new() { Source = "mcp:ref/upper", Kind = ToolKind.Read }),
            ("echo", new() { Source = "mcp:ref/echo", Kind = ToolKind.Read }),
            ("fail", new() { Source = "mcp:ref/fail", Kind = ToolKind.Read }));
        await using var servers = await ToolServers.ConnectAsync(options, ReferenceServer.Secrets(), Ct);
        var kit = new TestKit(options, servers.Tools);
        kit.Model.CallTools(("upper", """{ "text": "hi" }"""), ("fail", """{ "text": "hi" }""")).Reply("Done.");

        var result = await kit.RunAsync("dev", "Shout.", Ct);

        // The server lists one tool per page, upper first.
        Assert.Equal(["ref/echo", "ref/fail", "ref/upper"], servers.Tools.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            [("echo", "Repeats text."), ("fail", "Always fails."), ("upper", "Upper-cases text.")],
            kit.Model.Requests[0].Tools.Select(tool => (tool.Name, tool.Description)));
        Assert.Equal(
            [("<data source=\"tool:upper\">\nHI\n</data>", false), ("<data source=\"tool:fail\">\nfailed: it broke\n</data>", true)],
            result.Transcript[2].Content.Cast<ToolResultContent>().Select(content => (content.Text, content.IsError)));
    }

    [Theory]
    [InlineData(ToolServerTransport.Stdio)]
    [InlineData(ToolServerTransport.Http)]
    public async Task A_server_receives_its_credential_from_the_secret_source(ToolServerTransport transport)
    {
        await using var server = await ReferenceServer.StartAsync(transport);

        // The server refuses to initialize over stdio, and answers 401 over HTTP.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ToolServers.ConnectAsync(server.With(), ReferenceServer.Secrets("wrong"), Ct));

        Assert.True(error is InvalidOperationException or HttpRequestException, error.ToString());
    }

    [Fact]
    public async Task A_servers_tools_are_writes_unless_configured_as_reads_and_must_exist_on_the_server()
    {
        await using var server = await ReferenceServer.StartAsync(ToolServerTransport.Stdio);
        var options = server.With(("echo", new() { Source = "mcp:ref/echo" }), ("shout", new() { Source = "mcp:ref/shout", Kind = ToolKind.Read }));
        await using var servers = await ToolServers.ConnectAsync(options, ReferenceServer.Secrets(), Ct);

        var error = Assert.Throws<ConfigurationException>(() => new TestKit(options, servers.Tools));

        Assert.Equal(
            [("tools.shout.source", "tool server \"ref\" has no tool \"shout\"."), ("tools.echo", "write tool has no gate of its own.")],
            error.Errors.Select(error => (error.Path, error.Problem)));
    }
}
