using Sleepyshark.Officina;

namespace Samples.Extraction;

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
