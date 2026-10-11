using System.ComponentModel;

namespace BookshopAssistant;

/// <summary>A line of an order to place: which book, how many copies.</summary>
public sealed record OrderLine(
    [property: Description("The book's id.")] int BookId,
    [property: Description("How many copies, at least 1.")] int Quantity);
