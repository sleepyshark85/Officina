using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Records;
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

    // OUT-04, REC-01: before the turn and as a tool; a citation id may hold spaces.
    [Fact]
    public async Task Retrieved_passages_join_the_run_record_as_citations_that_answers_can_cite()
    {
        handbook = new FakeKnowledgeSource(Coverage.Covered, ("hb-4.2", "Refunds take 5 days."), ("Fees, section 2", "No fees."));
        var kit = Kit(new() { BeforeTurn = ["handbook"] }, ("search_handbook", new() { Source = "knowledge:handbook" }));
        kit.Model.CallTools(("search_handbook", """{ "question": "fees" }""")).Reply("5 days [cite:hb-4.2], and no fees [cite:Fees, section 2].");

        var result = await kit.RunAsync(Agent, Work, Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal(
            [
                new Citation("hb-4.2", "knowledge:handbook", "hb-4.2", "Refunds take 5 days."),
                new Citation("Fees, section 2", "knowledge:handbook", "Fees, section 2", "No fees."),
            ],
            result.Record.Select(entry => entry.Item));
    }

    // ING-02: a masked source's quotes are recorded with their tokens.
    [Fact]
    public async Task A_masked_sources_passages_are_cited_with_their_tokens()
    {
        handbook = new FakeKnowledgeSource(Coverage.Covered, ("hb-1", "Write to ann@example.com."));
        var options = Configure(new() { BeforeTurn = ["handbook"] });
        var kit = new TestKit(
            options with { Knowledge = new Dictionary<string, KnowledgeOptions> { ["handbook"] = new() { Use = "extension:Handbook", Mask = true } } },
            knowledge: new Dictionary<string, IKnowledgeSource> { ["Handbook"] = handbook });
        kit.Model.Reply("Done.");

        var result = await kit.RunAsync(Agent, Work, Ct);

        Assert.Equal(new Citation("hb-1", "knowledge:handbook", "hb-1", "Write to [email-1]."), Assert.Single(result.Record).Item);
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
            Capabilities = new() { Knowledge = new() { Enabled = true } },
            Agents = new Dictionary<string, AgentDefinition> { [Agent] = options.Agents[Agent] with { Context = new() { Retrieval = retrieval } } },
        };
    }

    private TestKit Kit(RetrievalOptions retrieval, params (string Name, ToolOptions Tool)[] tools) =>
        new(Configure(retrieval, tools), knowledge: new Dictionary<string, IKnowledgeSource> { ["Handbook"] = handbook });
}
