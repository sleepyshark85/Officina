using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Mcp.Tests;

/// <summary>
/// TEST-17: an application tool, a built-in tool and a tool server's tool are governed by the same permissions, gates
/// and audit. The provider-tool half is in the S03 pipeline tests and the S04 turn tests.
/// </summary>
public class GovernanceTests
{
    private static readonly Caller Owner = new("owner", null, new HashSet<string> { "lookup" }, new Dictionary<string, string>());

    [Theory]
    [InlineData("extension:lookup", """{ "text": "x" }""")]
    [InlineData("knowledge:handbook", """{ "question": "x" }""")]
    [InlineData("mcp:ref/echo", """{ "text": "x" }""")]
    public async Task Every_kind_of_tool_goes_through_the_same_permissions_gates_and_audit(string source, string arguments)
    {
        await using var server = await ReferenceServer.StartAsync(ToolServerTransport.Stdio);
        var options = server.With(("lookup", new() { Source = source, Kind = ToolKind.Write, Permissions = ["lookup"], Gates = ["ask"] })) with
        {
            Gates = new Dictionary<string, GateOptions> { ["ask"] = new() { Use = GateOptions.RequireApproval } },
            Knowledge = new Dictionary<string, KnowledgeOptions> { ["handbook"] = new() { Use = "extension:handbook" } },
        };
        await using var servers = await ToolServers.ConnectAsync(options, ReferenceServer.Secrets(), TestContext.Current.CancellationToken);
        var audit = new InMemoryAuditLog();
        var human = new ScriptedHuman().Answer(HumanAnswer.Deny).Answer(HumanAnswer.Approve);
        var pipeline = new ToolPipeline(
            options,
            new Dictionary<string, ITool>(servers.Tools) { ["lookup"] = new FakeTool(ToolKind.Read) },
            new Dictionary<string, IGate>(),
            new Dictionary<string, IKnowledgeSource> { ["handbook"] = new FakeKnowledgeSource(Coverage.Covered, ("hb-1", "x")) },
            audit,
            human,
            ReferenceServer.Secrets(),
            new FakeTimeProvider());

        var outcomes = new List<ToolErrorCategory?>();
        foreach (var caller in new[] { Caller.Anonymous, Owner, Owner })
        {
            var request = new ToolRequest("lookup", JsonDocument.Parse(arguments).RootElement);
            outcomes.Add(Assert.Single(await pipeline.RunAsync(new("run-1", "dev", caller), [request], TestContext.Current.CancellationToken)).Error);
        }

        Assert.Equal([ToolErrorCategory.NotAuthorised, ToolErrorCategory.PolicyViolation, null], outcomes);
        Assert.Equal(
            [
                ("permissions", AuditOutcome.Denied),
                ("gates.ask", AuditOutcome.Asked), ("human", AuditOutcome.Denied),
                ("gates.ask", AuditOutcome.Asked), ("human", AuditOutcome.Intent), (null, AuditOutcome.Completed),
            ],
            audit.Entries.Select(entry => (entry.DecidedBy, entry.Outcome)));
    }
}
