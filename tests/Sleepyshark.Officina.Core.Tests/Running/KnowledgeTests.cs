using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>Knowledge retrieved before the turn, as a tool, or both (CTX-04), and what "not covered" does (CTX-05).</summary>
public class KnowledgeTests
{
    private const string Work = "How long do refunds take?";

    private const string Found = "coverage: partlyCovered\nhb-4.2: Refunds take 5 days.";

    private FakeKnowledgeSource handbook = new(Coverage.PartlyCovered, ("hb-4.2", "Refunds take 5 days."));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Before_the_turn_the_work_is_searched_and_the_passages_join_the_volatile_context_as_data()
    {
        var kit = Kit(new() { BeforeTurn = ["handbook"] });
        kit.Model.Reply("5 days.");

        await kit.RunAsync(Agent, Work, Ct);

        Assert.Equal(new RetrievalQuery(Work, Caller.Anonymous, 8), Assert.Single(handbook.Queries));
        Assert.Equal(
            [Message.User(Work), Message.User($"<context>\n<data source=\"knowledge:handbook\">\n{Found}\n</data>\n</context>")],
            Assert.Single(kit.Model.Requests).History);
    }

    [Theory]
    [InlineData(true, AgentOutcome.HandedOff)]
    [InlineData(false, AgentOutcome.Completed)]
    public async Task Work_no_source_covers_ends_in_a_handoff_for_a_policy_gap_when_configured(bool handOff, AgentOutcome outcome)
    {
        handbook = new FakeKnowledgeSource(Coverage.NotCovered);
        var kit = Kit(new() { BeforeTurn = ["handbook"], HandOffWhenNotCovered = handOff });
        kit.Model.Reply("I could not find it.");

        var result = await kit.RunAsync(Agent, Work, Ct);

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(handOff ? HandoffReason.PolicyGap : null, result.Handoff?.Reason);
        Assert.Equal(handOff ? 0 : 1, kit.Model.Requests.Count);
    }

    [Fact]
    public async Task As_a_tool_the_agent_searches_when_it_chooses_and_both_ways_combine()
    {
        var kit = Kit(new() { BeforeTurn = ["handbook"] }, ("search_handbook", new() { Source = "knowledge:handbook" }));
        kit.Model.CallTools(("search_handbook", """{ "question": "refund fees" }""")).Reply("No fees.");

        var result = await kit.RunAsync(Agent, Work, Ct);

        Assert.Equal([Work, "refund fees"], handbook.Queries.Select(query => query.Question));
        Assert.Equal(
            "Searches the knowledge source handbook. Returns passages with their citations, and whether they cover the question.",
            Assert.Single(kit.Model.Requests[0].Tools).Description);
        Assert.Equal(
            new ToolResultContent("call-1", $"<data source=\"tool:search_handbook\">\n{Found}\n</data>", false),
            result.Transcript.SelectMany(message => message.Content).OfType<ToolResultContent>().Single());
    }

    [Fact]
    public void A_knowledge_source_the_application_has_not_registered_is_reported()
    {
        var error = Assert.Throws<ConfigurationException>(() => new TestKit(Configure(new())));

        Assert.Equal(
            ("knowledge.handbook.use", "knowledge source extension \"Handbook\" is not registered."),
            (Assert.Single(error.Errors).Path, error.Errors[0].Problem));
    }

    private static OfficinaOptions Configure(RetrievalOptions retrieval, params (string Name, ToolOptions Tool)[] tools)
    {
        var options = Options(tools);
        return options with
        {
            Knowledge = new Dictionary<string, KnowledgeOptions> { ["handbook"] = new() { Use = "extension:Handbook" } },
            Agents = new Dictionary<string, AgentDefinition> { [Agent] = options.Agents[Agent] with { Context = new() { Retrieval = retrieval } } },
        };
    }

    private TestKit Kit(RetrievalOptions retrieval, params (string Name, ToolOptions Tool)[] tools) =>
        new(Configure(retrieval, tools), knowledge: new Dictionary<string, IKnowledgeSource> { ["Handbook"] = handbook });
}
