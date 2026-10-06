using System.ComponentModel;

namespace Samples.Extraction;

/// <summary>What the triage agent extracts from a customer message: its typed output.</summary>
public sealed record Triage(
    [property: Description("What the message is about.")] Category Category,
    [property: Description("High when the customer is blocked or has been charged wrongly; low for a question that can wait.")] Urgency Urgency,
    [property: Description("The order number the message gives, such as A-1042, or null when it gives none.")] string? OrderNumber,
    [property: Description("One sentence on what the customer wants.")] string Summary);
