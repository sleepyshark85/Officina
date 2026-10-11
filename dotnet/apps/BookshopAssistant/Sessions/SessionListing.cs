namespace BookshopAssistant;

/// <summary>A session as <c>/sessions</c> lists it; <paramref name="Stale"/> when it has no summary, or changed since.</summary>
public sealed record SessionListing(
    string Id, string StaffMember, string? Title, string? Summary, IReadOnlyList<string> Changes, decimal Cost, DateTimeOffset Updated, bool Stale);
