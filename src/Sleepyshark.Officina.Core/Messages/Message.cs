using System.Collections.Immutable;

namespace Sleepyshark.Officina.Core.Messages;

/// <summary>A message in a conversation: a role and at least one piece of content (MSG-01). It cannot be changed once created (MSG-03).</summary>
public sealed record Message
{
    /// <param name="role">Who the message is from.</param>
    /// <param name="content">At least one piece of content.</param>
    /// <param name="turnScoped">Whether it is a system message the provider shows for one model call only.</param>
    public Message(Role role, IEnumerable<Content> content, bool turnScoped = false)
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
        TurnScoped = turnScoped;
    }

    public Role Role { get; }

    public ImmutableArray<Content> Content { get; }

    /// <summary>
    /// Whether the provider shows this system message for one model call only, and clears it after (CTX-10). It stays in
    /// the history, so later calls still start with what earlier calls sent.
    /// </summary>
    public bool TurnScoped { get; }

    public static Message User(string text) => new(Role.User, [new TextContent(text)]);

    public static Message Assistant(string text) => new(Role.Assistant, [new TextContent(text)]);

    public static Message System(string text) => new(Role.System, [new TextContent(text)]);

    public bool Equals(Message? other) =>
        other is not null && Role == other.Role && TurnScoped == other.TurnScoped && Content.SequenceEqual(other.Content);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Role);
        hash.Add(TurnScoped);
        foreach (var piece in Content)
        {
            hash.Add(piece);
        }

        return hash.ToHashCode();
    }
}
