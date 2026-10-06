using System.ComponentModel;
using Sleepyshark.Officina;

namespace Samples.Extraction;

/// <summary>What a customer message is about.</summary>
public enum Category
{
    Billing,
    Delivery,
    Product,
    Account,
    Other,
}

/// <summary>How soon a customer message needs an answer.</summary>
public enum Urgency
{
    Low,
    Normal,
    High,
}

/// <summary>What the triage agent extracts from a customer message: its typed output.</summary>
public sealed record Triage(
    [property: Description("What the message is about.")] Category Category,
    [property: Description("High when the customer is blocked or has been charged wrongly; low for a question that can wait.")] Urgency Urgency,
    [property: Description("The order number the message gives, such as A-1042, or null when it gives none.")] string? OrderNumber,
    [property: Description("One sentence on what the customer wants.")] string Summary);

/// <summary>
/// Extraction and classification: a stateless agent with only a model, instructions and typed output. Each message is
/// one run on a new, discarded conversation, ending at the model's <c>end</c> after one call.
/// </summary>
public static class TicketTriage
{
    public static Agent Create(IModel model) => new()
    {
        Name = "triage",
        Model = model,
        Instructions = Instructions,
        Output = OutputContract.For<Triage>(),
    };

    /// <summary>Classifies <paramref name="message"/>; null when the run did not complete, as on a refusal or invalid output.</summary>
    public static async Task<Triage?> ClassifyAsync(Agent agent, string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var result = await agent.RunAsync(message, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (result as Completed)?.Output as Triage;
    }

    private const string Instructions = """
        You triage messages that customers send to an online shop's support team. The user message is one customer
        message. Classify what it is about and how urgent it is, copy the order number it gives (if any) exactly as
        written, and summarize in one sentence what the customer wants. Do not answer the customer.
        """;
}
