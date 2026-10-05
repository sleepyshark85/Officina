using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina;

/// <summary>
/// The append-only messages between an agent and its model (AGT-06). The host owns it and stores it as JSON between runs;
/// the core only ever appends. One run at a time may use a conversation.
/// </summary>
public sealed class Conversation
{
    /// <summary>
    /// The prefix fingerprint of the agent definition the conversation was started with; null before its first run. A run
    /// of a definition with another fingerprint fails without calling the model (CTX-04).
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; private set; }

    /// <summary>The messages, oldest first. Each read is a snapshot that later appends do not change.</summary>
    [JsonInclude]
    [JsonPropertyName("messages")]
    public ImmutableArray<Message> Messages { get; private set; } = [];

    internal void Bind(string fingerprint) => Fingerprint = fingerprint;

    internal void Append(Message message) => Messages = Messages.Add(message);
}

/// <summary>Who a message is from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Role>))]
public enum Role
{
    [JsonStringEnumMemberName("user")]
    User,

    [JsonStringEnumMemberName("assistant")]
    Assistant,

    /// <summary>The host, speaking with operator authority after the cached prefix: the run context (CTX-02).</summary>
    [JsonStringEnumMemberName("operator")]
    Operator,
}

/// <summary>One message: a role and at least one content block. It never changes once created.</summary>
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

    public override int GetHashCode() => HashCode.Combine(Role, Blocks.Length);
}

/// <summary>
/// One piece of a message (MDL-05). A block the model produced keeps the provider's JSON exactly as received in
/// <see cref="Raw"/>, which is stored and replayed byte for byte; the core reads only <see cref="Text"/>. A block the core
/// made has no raw form, and the provider adapter renders it.
/// </summary>
[JsonConverter(typeof(ContentBlockConverter))]
public sealed record ContentBlock
{
    /// <param name="text">The text, for a text block; null for any other block.</param>
    /// <param name="raw">The provider's JSON for the block, as received; null for a block the core made.</param>
    public ContentBlock(string? text, string? raw = null)
    {
        if (text is null && raw is null)
        {
            throw new ArgumentException("A block needs text, raw JSON or both.");
        }

        if (raw is not null)
        {
            using var _ = JsonDocument.Parse(raw);
        }

        Text = text;
        Raw = raw;
    }

    public string? Text { get; }

    public string? Raw { get; }
}

/// <summary>Writes a block's raw JSON verbatim and reads it back as the exact text it was written as (AGT-06).</summary>
internal sealed class ContentBlockConverter : JsonConverter<ContentBlock>
{
    public override ContentBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        var raw = root.TryGetProperty("raw", out var rawElement) ? rawElement.GetRawText() : null;
        return new ContentBlock(text, raw);
    }

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Text is not null)
        {
            writer.WriteString("text", value.Text);
        }

        if (value.Raw is not null)
        {
            writer.WritePropertyName("raw");
            writer.WriteRawValue(value.Raw);
        }

        writer.WriteEndObject();
    }
}
