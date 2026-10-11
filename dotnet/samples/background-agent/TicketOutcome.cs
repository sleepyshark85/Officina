using System.ComponentModel;

namespace Samples.BackgroundAgent;

/// <summary>What a job reports about its ticket: its typed output.</summary>
public sealed record TicketOutcome(
    [property: Description("Answered when the note answers the customer; escalated when a person must act.")] Resolution Resolution,
    [property: Description("One sentence for the support team on what was done and why.")] string Report);
