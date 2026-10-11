using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina;

/// <summary>One message: a role and at least one content block. Never changes once created.</summary>
public sealed record Message
{
    [JsonConstructor]
    public Message(Role role, ImmutableArray<ContentBlock> blocks)
    {
        if (blocks.IsDefaultOrEmpty || blocks.Any(block => block is null))
        {
            throw new ArgumentException("A message needs at least one block, and no null blocks.", nameof(blocks));
        }

        Role = role;
        Blocks = blocks;
    }

    [JsonPropertyName("role")]
    public Role Role { get; }

    [JsonPropertyName("blocks")]
    public ImmutableArray<ContentBlock> Blocks { get; }

    /// <summary>The text of the message's text blocks, joined.</summary>
    [JsonIgnore]
    public string Text => string.Concat(Blocks.Select(block => block.Text));

    public static Message Of(Role role, string text) => new(role, [new ContentBlock(text)]);

    public bool Equals(Message? other) => other is not null && Role == other.Role && Blocks.SequenceEqual(other.Blocks);

    // Equal messages have equal roles and block counts; hashing the blocks would cost more than it saves.
    public override int GetHashCode() => HashCode.Combine(Role, Blocks.Length);
}
