using Samples.Extraction;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;

namespace Samples.Tests;

/// <summary>GEN-06: the extraction and classification sample, offline, with only the model scripted.</summary>
public class ExtractionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_message_is_classified_into_typed_output_in_one_stateless_call_without_tools()
    {
        var model = new ScriptedModel()
            .Reply("""{"category":"Delivery","urgency":"High","orderNumber":"A-1042","summary":"The parcel for order A-1042 has not arrived."}""")
            .Reply("""{"category":"Account","urgency":"Low","orderNumber":null,"summary":"The customer wants to change their email address."}""");
        var agent = TicketTriage.Create(model);

        var first = await TicketTriage.ClassifyAsync(agent, "My order A-1042 still hasn't arrived and I need it tomorrow!", Ct);
        var second = await TicketTriage.ClassifyAsync(agent, "How do I change my email address?", Ct);

        Assert.Equal(new Triage(Category.Delivery, Urgency.High, "A-1042", "The parcel for order A-1042 has not arrived."), first);
        Assert.Equal(new Triage(Category.Account, Urgency.Low, null, "The customer wants to change their email address."), second);

        // Each message is a run of its own: one request, with no tools, the output schema and a new conversation.
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, request =>
        {
            Assert.Empty(request.Tools);
            Assert.Equal(agent.Output!.Schema, request.OutputSchema);
            Assert.Equal([Role.User], request.Messages.Select(message => message.Role));
        });
    }

    [Fact]
    public async Task TEST_02_every_classification_sends_the_same_prefix()
    {
        var model = new ScriptedModel()
            .Reply("""{"category":"Billing","urgency":"Normal","orderNumber":null,"summary":"A question about an invoice."}""")
            .Reply("""{"category":"Product","urgency":"Low","orderNumber":null,"summary":"A question about a size."}""");

        // An agent built afresh for each message, as in a new process, sends the same tools, instructions and schema.
        await TicketTriage.ClassifyAsync(TicketTriage.Create(model), "Why was I charged twice?", Ct);
        await TicketTriage.ClassifyAsync(TicketTriage.Create(model), "Does this shirt run small?", Ct);

        // A stateless run's conversation is new each time, so its prefix is everything before the messages.
        Assert.Empty(PrefixStability.Problems(model.Requests.Select(request => request with { Messages = [] })));
    }

    [Fact]
    public async Task Output_that_does_not_match_the_type_fails_the_run_and_classifies_nothing()
    {
        var model = new ScriptedModel()
            .Reply("""{"category":"Shipping","urgency":"High","orderNumber":null,"summary":"Late."}""")
            .Reply("""{"category":"Shipping","urgency":"High","orderNumber":null,"summary":"Late."}""");
        var agent = TicketTriage.Create(model);

        Assert.Null(await TicketTriage.ClassifyAsync(agent, "Where is my parcel?", Ct));
        Assert.Equal(FailureReason.InvalidOutput, Assert.IsType<Failed>(await agent.RunAsync("Where is my parcel?", cancellationToken: Ct)).Reason);
    }
}
