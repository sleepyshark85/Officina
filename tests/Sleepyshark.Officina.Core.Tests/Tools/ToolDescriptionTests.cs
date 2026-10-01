using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Tools;

/// <summary>An agent with many tools can load their descriptions on demand (TOOL-12).</summary>
public class ToolDescriptionTests
{
    private const string IssueSchema = """{ "type": "object", "properties": { "title": { "type": "string" } }, "required": ["title"] }""";

    private readonly ToolSetup setup = new();

    public ToolDescriptionTests() => setup.Tools["create_issue"] = new FakeTool(ToolKind.Write, IssueSchema);

    [Fact]
    public async Task On_demand_the_model_is_offered_names_and_reads_a_tools_description_when_it_needs_it()
    {
        var pipeline = setup.Create(OnDemand(("create_issue", Extension("create_issue") with { GateExemption = "Tests only." })));

        var offered = pipeline.Offered(Agent);
        var described = JsonDocument.Parse((await RunAsync(pipeline, OfficinaOptions.DescribeTool, """{ "name": "create_issue" }""")).Content).RootElement;

        Assert.Equal(["create_issue", OfficinaOptions.DescribeTool], offered.Select(tool => tool.Name));
        Assert.Equal("""{ "type": "object" }""", offered[0].InputSchema?.GetRawText());
        Assert.StartsWith("Call describe_tool", offered[0].Description, StringComparison.Ordinal);
        Assert.Equal("A tool for tests.", described.GetProperty("description").GetString());
        Assert.True(JsonElement.DeepEquals(Args(IssueSchema), described.GetProperty("inputSchema")));

        // The pipeline still checks the arguments against the tool's own schema.
        Assert.Equal(ToolErrorCategory.InvalidArguments, (await RunAsync(pipeline, "create_issue")).Error);
    }

    [Fact]
    public async Task Describing_a_tool_the_agent_is_not_offered_is_an_unknown_tool()
    {
        var pipeline = setup.Create(OnDemand());

        var result = await RunAsync(pipeline, OfficinaOptions.DescribeTool, """{ "name": "create_issue" }""");

        Assert.Equal((ToolErrorCategory.UnknownTool, "unknown tool: create_issue"), (result.Error, result.Content));
    }

    [Fact]
    public void A_tool_of_the_agent_cannot_take_the_name_describe_tool()
    {
        var error = Assert.Single(OnDemand((OfficinaOptions.DescribeTool, Extension("create_issue") with { GateExemption = "Tests only." })).Validate());

        Assert.Equal((ValidationPhase.Tools, "agents.dev.toolDescriptionsOnDemand"), (error.Phase, error.Path));
    }

    private static OfficinaOptions OnDemand(params (string Name, ToolOptions Tool)[] tools)
    {
        var options = Options(tools);
        return options with { Agents = new Dictionary<string, AgentDefinition> { [Agent] = options.Agents[Agent] with { ToolDescriptionsOnDemand = true } } };
    }
}
