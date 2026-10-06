using System.ComponentModel;

namespace BookshopAssistant;

/// <summary>What the summarizer writes about a session.</summary>
public sealed record SessionSummary(
    [property: Description("A title of a few words, naming the customers, books or orders the session was about.")] string Title,
    [property: Description("One to three sentences on what the staff member asked and what came of it.")] string Summary,
    [property: Description("Each change made to the shop's data, such as a customer added, an order placed or cancelled, or a restock, with its ids. Empty when nothing changed.")]
    IReadOnlyList<string> Changes);
