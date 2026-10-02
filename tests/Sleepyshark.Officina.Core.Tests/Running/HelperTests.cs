using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>
/// Helper agents (TEAM-07) and the hand-off to a human (EGR-04). The agent <c>dev</c> may start <c>researcher</c> with
/// <c>helper</c>; the researcher may <c>look</c>, which needs the permission <c>look</c>, and may itself start researchers.
/// </summary>
public class HelperTests
{
    private static readonly Caller Ann = new("ann", null, new HashSet<string> { "look" }, new Dictionary<string, string>());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // TEAM-07: the helper is an agent of its own, on the work it is given, and its output is the tool's result.
    [Fact]
    public async Task An_agent_starts_a_helper_and_gets_its_output()
    {
        var kit = Kit();
        Script(kit, "Fix the parser.").CallTools(("helper", """{ "agent": "researcher", "work": "Find the grammar." }""")).Reply("Fixed.");
        Script(kit, "Find the grammar.").Reply("It is in grammar.txt.");

        var work = new Work(Agent, "Fix the parser.") { Caller = Ann };
        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal((AgentOutcome.Completed, "Fixed."), (result.Outcome, result.Output));
        var parent = kit.Model.Requests.Last(request => ScriptedModelProvider.WorkOf(request) == "Fix the parser.");
        Assert.Contains("It is in grammar.txt.", parent.History.SelectMany(message => message.Content).OfType<ToolResultContent>().Single().Text, StringComparison.Ordinal);
        Assert.Contains(await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct), coreEvent => coreEvent.Agent == "researcher[dev.1]" && coreEvent.Payload is ModelCallEnded);
    }

    // TEAM-07: only the helpers its definition lists, no deeper than the depth limit, and no more per turn than the count limit.
    [Theory]
    [InlineData("writer", "dev may not start writer; it may start: researcher.")]
    [InlineData("count", "a turn may start at most 1 helpers.")]
    [InlineData("depth", "a helper may not start helpers deeper than 1.")]
    public async Task Helpers_stay_within_what_the_role_allows_and_the_limits(string case_, string refusal)
    {
        var kit = Kit(team => team with { HelperDepth = 1, HelperCount = 1 });
        var first = case_ == "writer" ? "writer" : "researcher";
        Script(kit, "Fix the parser.").CallTools(("helper", $$"""{ "agent": "{{first}}", "work": "Find the grammar." }"""))
            .CallTools(("helper", """{ "agent": "researcher", "work": "Find the tests." }""")).Reply("Fixed.");
        Script(kit, "Find the grammar.").CallTools(("helper", """{ "agent": "researcher", "work": "Look deeper." }""")).Reply("Found.");
        Script(kit, "Find the tests.").Reply("Found too.");

        await kit.Runner.RunAsync(new Work(Agent, "Fix the parser.") { Caller = Ann }, Ct);

        var results = kit.Model.Requests.SelectMany(request => request.History).SelectMany(message => message.Content).OfType<ToolResultContent>().Select(content => content.Text);
        Assert.Contains(results, text => text.Contains(refusal, StringComparison.Ordinal));
    }

    // TEAM-07: a helper's permissions are within its parent's, and what it spends counts against its parent's turn.
    [Fact]
    public async Task A_helpers_permissions_and_budget_come_out_of_its_parents()
    {
        var kit = Kit(configure: options => options with
        {
            Agents = new Dictionary<string, AgentDefinition>(options.Agents)
            {
                [Agent] = options.Agents[Agent] with { Permissions = [], Budget = new() { Turn = new() { Tokens = 1500 } } },
                ["researcher"] = options.Agents["researcher"] with { HandOffOnPolicyGap = false }, // the refusal goes back to its model
            },
        });
        Script(kit, "Fix the parser.").CallTools(("helper", """{ "agent": "researcher", "work": "Find the grammar." }""")).Reply("Fixed.");
        Script(kit, "Find the grammar.")
            .Reply(new ContentReceived(new ToolUseContent("look-1", "look", Args("{}"))), new UsageReported(new Usage(1000, 0, 0, 0)), new Stopped(StopReason.WantsTools))
            .Reply(new TextDelta("Found."), new UsageReported(new Usage(600, 0, 0, 0)), new Stopped(StopReason.Finished)); // 1600 tokens of the parent's 1500

        var result = await kit.Runner.RunAsync(new Work(Agent, "Fix the parser.") { Caller = Ann }, Ct);

        var helper = kit.Model.Requests.Last(request => ScriptedModelProvider.WorkOf(request) == "Find the grammar.");
        Assert.Equal("not authorised: the caller does not hold the permission look", helper.History.SelectMany(message => message.Content).OfType<ToolResultContent>().Single().Text[(@"<data source=""tool:look"">" + "\n").Length..^"\n</data>".Length]);
        Assert.Equal((HandoffReason.BudgetExhausted, "the turn's token budget is used up"), (result.Handoff?.Reason, result.Handoff?.Detail));
    }

    // EGR-04: the model hands its work to a human by calling the tool for it; the turn ends in a handoff to a human.
    [Fact]
    public async Task An_agent_hands_its_work_to_a_human_with_the_tool_for_it()
    {
        var kit = Kit();
        Script(kit, "Delete the database.").CallTools(("handoff", """{ "reason": "Deleting data needs the owner." }"""));

        var result = await kit.Runner.RunAsync(new Work(Agent, "Delete the database.") { Caller = Ann }, Ct);

        Assert.Equal(
            (AgentOutcome.HandedOff, HandoffReason.PolicyGap, ToolResult.Human, "the agent asked for a human: Deleting data needs the owner."),
            (result.Outcome, result.Handoff!.Reason, result.Handoff.To, result.Handoff.Detail));
    }

    private static ModelScript Script(TestKit kit, string work) => kit.Model.When(request => ScriptedModelProvider.WorkOf(request) == work);

    private static TestKit Kit(Func<TeamOptions, TeamOptions>? team = null, Func<OfficinaOptions, OfficinaOptions>? configure = null)
    {
        var options = Options(
            ("helper", new() { Source = "builtin:team.start_helper" }),
            ("handoff", new() { Source = "builtin:human.request_handoff" }),
            ("look", Extension("look") with { Permissions = ["look"] }));
        options = options with
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                [Agent] = new() { Instructions = "Develop.", Tools = ["dev"], Helpers = ["researcher"] },
                ["researcher"] = new() { Instructions = "Research.", Tools = ["research"], Helpers = ["researcher"] },
                ["writer"] = new() { Instructions = "Write." },
            },
            ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["dev"] = ["helper", "handoff"], ["research"] = ["helper", "look"] },
            Capabilities = new() { Team = (team ?? (settings => settings))(new() { Enabled = true }), TaskBoard = new() { Enabled = true } },
            Providers = new Dictionary<string, ProviderOptions>
            {
                [ProviderOptions.ClaudeName] = ProviderOptions.Claude with { Prices = new Dictionary<string, ModelPrice> { ["claude-opus-5-5"] = new() { Input = 1 } } },
            },
        };
        return new TestKit((configure ?? (value => value))(options), new Dictionary<string, ITool> { ["look"] = new FakeTool(ToolKind.Read) });
    }
}
