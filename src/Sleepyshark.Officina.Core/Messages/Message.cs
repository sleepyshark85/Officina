using System.Collections.Immutable;

namespace Sleepyshark.Officina.Core.Messages;

/// <summary>A message in a conversation: a role and at least one piece of content (MSG-01). It cannot be changed once created (MSG-03).</summary>
public sealed record Message
{
    public Message(Role role, IEnumerable<Content> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var pieces = content.ToImmutableArray();
        if (pieces.IsEmpty)
        {
            throw new ArgumentException("A message needs at least one piece of content.", nameof(content));
        }

        if (pieces.Any(piece => piece is null))
        {
            throw new ArgumentException("Message content cannot contain null.", nameof(content));
        }

        Role = role;
        Content = pieces;
    }

    public Role Role { get; }

    public ImmutableArray<Content> Content { get; }

    public static Message User(string text) => new(Role.User, [new TextContent(text)]);

    public static Message Assistant(string text) => new(Role.Assistant, [new TextContent(text)]);

    public bool Equals(Message? other) =>
        other is not null && Role == other.Role && Content.SequenceEqual(other.Content);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Role);
        foreach (var piece in Content)
        {
            hash.Add(piece);
        }

        return hash.ToHashCode();
    }
}
