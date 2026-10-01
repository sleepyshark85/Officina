using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Tools;

/// <summary>Tools are checked when the pipeline is built, before anything runs (TOOL-02, INV-04, CFG-13).</summary>
public class ToolValidationTests
{
    private readonly ToolSetup setup = new();

    // TEST-10.
    [Theory]
    [InlineData(ToolKind.Write, null, false)]
    [InlineData(ToolKind.Read, ToolKind.Write, false)]
    [InlineData(ToolKind.Read, null, true)]
    public void A_write_tool_without_a_gate_of_its_own_fails_validation(ToolKind declared, ToolKind? configured, bool irreversible)
    {
        setup.Tools["create_issue"] = new FakeTool(declared);

        var error = Assert.Single(Errors(Options(("create_issue", Extension("create_issue") with { Kind = configured, Irreversible = irreversible }))));

        Assert.Equal((ValidationPhase.Tools, "tools.create_issue", "write tool has no gate of its own."), (error.Phase, error.Path, error.Problem));
        Assert.Equal("Add \"gates\": [...] or \"gateExemption\": \"<reason>\".", error.Fix);
    }

    [Fact]
    public void A_write_tool_with_a_gate_or_a_stated_exemption_is_valid()
    {
        setup.Tools["create_issue"] = new FakeTool(ToolKind.Write);
        setup.Tools["write_note"] = new FakeTool(ToolKind.Write);
        var options = Options(
            ("create_issue", Extension("create_issue") with { Gates = ["main-approval"] }),
            ("write_note", Extension("write_note") with { GateExemption = "Writes only to a scratch folder." })) with
        {
            Gates = new Dictionary<string, GateOptions> { ["main-approval"] = new() { Use = GateOptions.RequireApproval } },
        };

        Assert.Empty(Errors(options));
    }

    [Fact]
    public void Tools_and_gates_the_application_has_not_registered_are_reported()
    {
        var options = Options(("create_issue", Extension("create_issue") with { Gates = ["dedupe"] })) with
        {
            Gates = new Dictionary<string, GateOptions> { ["dedupe"] = new() { Use = "extension:Acme.Dedupe" } },
        };

        Assert.Equal(
            [("tools.create_issue.source", "tool extension \"create_issue\" is not registered."), ("gates.dedupe.use", "gate extension \"Acme.Dedupe\" is not registered.")],
            Errors(options).Select(error => (error.Path, error.Problem)));
    }

    [Fact]
    public void An_input_schema_that_is_not_valid_JSON_Schema_is_reported()
    {
        setup.Tools["search"] = new FakeTool(ToolKind.Read, inputSchema: """{ "type": 5 }""");

        var error = Assert.Single(Errors(Options(("search", Extension("search")))));

        Assert.Equal((ValidationPhase.Tools, "tools.search"), (error.Phase, error.Path));
        Assert.StartsWith("the input schema of search is not valid JSON Schema: ", error.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Conditions_are_checked_against_the_arguments_of_every_tool_they_apply_to()
    {
        setup.Tools["push"] = new FakeTool(ToolKind.Write, """{ "type": "object", "properties": { "branch": { "type": "string" } } }""");
        var options = Options(("push", Extension("push") with { Gates = ["no-force"] })) with
        {
            Gates = new Dictionary<string, GateOptions> { ["no-force"] = new() { Use = GateOptions.Deny, When = new() { Field = "args.force", Is = "true" } } },
            Policies = new() { PermissionRules = [new() { Tool = "push", When = new() { Field = "args.branch", Gt = 1 } }] },
        };

        Assert.Equal(
            [
                ("policies.permissionRules[0].when", "for tool push: field args.branch is not a number, so gt, gte, lt and lte can never hold."),
                ("gates.no-force.when", "for tool push: field args.force is not in the tool's arguments."),
            ],
            Errors(options).Select(error => (error.Path, error.Problem)));
        Assert.All(Errors(options), error => Assert.Equal(ValidationPhase.Conditions, error.Phase));
    }

    [Fact]
    public void Settings_that_need_no_tools_are_checked_with_the_rest_of_the_configuration()
    {
        var options = Options(
            ("search", new() { Source = "builtin:workspace.search", Gates = ["dedup"] }),
            ("web_search", new() { Source = "provider:web_search" })) with
        {
            ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["all"] = ["search", "web_serch"] },
            Gates = new Dictionary<string, GateOptions> { ["dedupe"] = new() { Use = "builtin:rate-limit" } },
            Policies = new() { PermissionRules = [new() { Tool = "search", Action = PolicyAction.Route }] },
        };

        Assert.Equal(
            [
                (ValidationPhase.Shape, "tools.search.source", "\"builtin:workspace.search\" is not a tool source."),
                (ValidationPhase.Shape, "gates.dedupe.use", "\"builtin:rate-limit\" is not a gate."),
                (ValidationPhase.Shape, "policies.permissionRules[0].to", "is required to route."),
                (ValidationPhase.References, "tools.search.gates", "gate \"dedup\" does not exist."),
                (ValidationPhase.References, "toolSets.all", "tool \"web_serch\" does not exist."),
                (ValidationPhase.Tools, "tools.web_search.reason", "is required for a provider tool."),
            ],
            options.Validate().Select(error => (error.Phase, error.Path, error.Problem)));
    }

    [Fact]
    public void An_agent_naming_a_missing_tool_set_is_reported()
    {
        var options = Options() with
        {
            Agents = new Dictionary<string, AgentDefinition> { [Agent] = new() { Instructions = "Work.", Tools = ["fils"] } },
        };

        var error = Assert.Single(options.Validate());

        Assert.Equal(("agents.dev.tools", "tool set \"fils\" does not exist."), (error.Path, error.Problem));
    }

    private ConfigurationError[] Errors(OfficinaOptions options)
    {
        try
        {
            setup.Create(options);
            return [];
        }
        catch (ConfigurationException exception)
        {
            return [.. exception.Errors];
        }
    }
}
