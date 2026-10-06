using System.ComponentModel;
using System.Text.Json;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Tests.Agents;

namespace Sleepyshark.Officina.Tests;

/// <summary>Typed output (OUT-01, OUT-02, GEN-05), stateless runs (GEN-03), and TEST-08's refusal of schemas outside the subset.</summary>
public class OutputTests
{
    public sealed record Summary([property: Description("A short title.")] string Title, IReadOnlyList<string> Changes, int? Score = null);

    public sealed record Node(string Name, IReadOnlyList<Node> Children);

    public sealed record Other(string Name);

    public sealed record Counts(Dictionary<string, int> PerBook);

    public sealed record Titled
    {
        public Titled(string title) => Title = title.Length > 0 ? title : throw new ArgumentException("The title is empty.", nameof(title));

        public string Title { get; }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Agent Typed(ScriptedModel model) => With(model) with { Output = OutputContract.For<Summary>() };

    [Fact]
    public async Task A_valid_reply_completes_with_the_typed_output_and_the_request_carries_the_schema_as_exported()
    {
        var model = new ScriptedModel().Reply("""{"title":"Restock","changes":["Book 320: +2 copies"],"score":null}""");
        var agent = Typed(model);

        var result = await agent.RunAsync("Summarize.", cancellationToken: Ct);

        var completed = Assert.IsType<Completed>(result);
        var summary = Assert.IsType<Summary>(completed.Output);
        Assert.Equal(("Restock", "Book 320: +2 copies", null), (summary.Title, Assert.Single(summary.Changes), summary.Score));
        Assert.Equal(agent.Output!.Schema, Assert.Single(model.Requests).Prefix.OutputSchema);
        Assert.Contains("\"additionalProperties\":false", agent.Output.Schema, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"title":"Restock"}""", "/changes: is required")]
    [InlineData("""{"title":"Restock","changes":[1]}""", "/changes/0: must be string")]
    [InlineData("""{"title":"Restock","changes":[],"extra":true}""", "/extra: is not allowed")]
    [InlineData("""{"title":"Restock","changes":[]""", "could not be read as Summary")]
    [InlineData("Here is the summary.", "could not be read as Summary")]
    public async Task Output_that_fails_to_validate_or_deserialize_ends_the_run_as_failed_with_the_errors_and_no_correction_round(string reply, string error)
    {
        var model = new ScriptedModel().Reply(reply);

        var result = await Typed(model).RunAsync("Summarize.", cancellationToken: Ct);

        var failed = Assert.IsType<Failed>(result);
        Assert.Equal(FailureReason.InvalidOutput, failed.Reason);
        Assert.Contains(error, failed.Error, StringComparison.Ordinal);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task An_exception_from_the_output_type_s_constructor_ends_the_run_as_failed()
    {
        var model = new ScriptedModel().Reply("""{"title":""}""");

        var result = await (With(model) with { Output = OutputContract.For<Titled>() }).RunAsync("Summarize.", cancellationToken: Ct);

        var failed = Assert.IsType<Failed>(result);
        Assert.Equal(FailureReason.InvalidOutput, failed.Reason);
        Assert.Contains("The title is empty.", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_with_typed_output_stops_rather_than_failing()
    {
        var model = new ScriptedModel().Reply(new ModelStopped(ModelStopReason.Refusal, "cyber"));

        var result = await Typed(model).RunAsync("Summarize.", cancellationToken: Ct);

        Assert.Equal(StopReason.Refusal, Assert.IsType<Stopped>(result).Reason);
    }

    [Fact]
    public void A_type_whose_schema_is_outside_the_subset_is_refused_when_the_contract_is_defined()
    {
        var refused = Assert.Throws<ArgumentException>(OutputContract.For<Node>);

        Assert.Contains("outside the supported subset", refused.Message, StringComparison.Ordinal);
        var open = Assert.Throws<ArgumentException>(OutputContract.For<Counts>);
        Assert.Contains("'/properties/perBook' is an open object", open.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_output_schema_is_part_of_the_prefix_fingerprint()
    {
        var model = new ScriptedModel().Reply("""{"title":"A","changes":[]}""");
        var agent = Typed(model);
        var conversation = new Conversation();
        await agent.RunAsync(conversation, "Summarize.", cancellationToken: Ct);

        Assert.True((agent with { Output = OutputContract.For<Summary>() }).CanContinue(conversation));
        Assert.False((agent with { Output = OutputContract.For<Other>() }).CanContinue(conversation));
        Assert.False((agent with { Output = null }).CanContinue(conversation));
    }

    [Fact]
    public async Task A_stateless_run_starts_a_new_conversation_each_time()
    {
        var model = new ScriptedModel().Reply("One.").Reply("Two.");
        var agent = With(model);

        await agent.RunAsync("First.", cancellationToken: Ct);
        var second = await agent.RunAsync("Second.", cancellationToken: Ct);

        Assert.Equal("Two.", Assert.IsType<Completed>(second).Text);
        Assert.Equal("Second.", Assert.Single(model.Requests[1].Messages).Text);
    }

    [Fact]
    public void The_exported_schema_names_members_in_camel_case_and_marks_the_required_ones()
    {
        using var schema = JsonDocument.Parse(OutputContract.For<Summary>().Schema);

        Assert.Equal(["title", "changes"], schema.RootElement.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal("A short title.", schema.RootElement.GetProperty("properties").GetProperty("title").GetProperty("description").GetString());
    }
}
