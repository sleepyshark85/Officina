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
    private int running;

    /// <summary>
    /// Identifies the conversation in the audit trail (AUD-03). A new conversation gets a random id; a host may give its
    /// own, such as its session id.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("id")]
    [JsonPropertyOrder(-1)]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

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

    internal void StartRun()
    {
        if (Interlocked.Exchange(ref running, 1) == 1)
        {
            throw new InvalidOperationException("Another run is using this conversation; one run at a time may use it.");
        }
    }

    internal void EndRun() => Volatile.Write(ref running, 0);

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

    // Equal messages have equal roles and block counts; hashing the blocks too would cost more than it saves.
    public override int GetHashCode() => HashCode.Combine(Role, Blocks.Length);
}

/// <summary>
/// One piece of a message (MDL-05). A block the model produced keeps the provider's JSON exactly as received in
/// <see cref="Raw"/>, which is stored and replayed byte for byte; the core reads only the neutral views the provider
/// adapter gives it, <see cref="Text"/> and <see cref="ToolCall"/>. A block the core made, such as a
/// <see cref="ToolResult"/>, has no raw form, and the provider adapter renders it.
/// </summary>
[JsonConverter(typeof(ContentBlockConverter))]
public sealed record ContentBlock
{
    /// <param name="text">The text, for a text block; null for any other block.</param>
    /// <param name="raw">The provider's JSON for the block, as received; null for a block the core made.</param>
    /// <param name="toolCall">The neutral view of the call, for a block that requests a tool.</param>
    public ContentBlock(string? text, string? raw = null, ToolCall? toolCall = null)
        : this(text, raw, toolCall, null)
    {
    }

    /// <summary>A tool's result, made by the core.</summary>
    public ContentBlock(ToolResult toolResult)
        : this(null, null, null, toolResult ?? throw new ArgumentNullException(nameof(toolResult)))
    {
    }

    internal ContentBlock(string? text, string? raw, ToolCall? toolCall, ToolResult? toolResult)
    {
        if (text is null && raw is null && toolResult is null)
        {
            throw new ArgumentException("A block needs text, raw JSON or a tool result.");
        }

        if (raw is not null)
        {
            using var _ = JsonDocument.Parse(raw);
        }

        Text = text;
        Raw = raw;
        ToolCall = toolCall;
        ToolResult = toolResult;
    }

    public string? Text { get; }

    public string? Raw { get; }

    public ToolCall? ToolCall { get; }

    public ToolResult? ToolResult { get; }
}

/// <summary>The neutral view of a tool call the model requested.</summary>
/// <param name="Id">The provider's id for the call, which its result answers.</param>
/// <param name="Name">The tool's name.</param>
/// <param name="Input">The input, as JSON text.</param>
public sealed record ToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("input")] string Input);

/// <summary>The result of a tool call, as the model gets it; a failure of any kind is an error result (TOOL-05).</summary>
/// <param name="CallId">The id of the call it answers.</param>
/// <param name="Content">What the tool returned, or why the call failed.</param>
/// <param name="IsError">Whether the call failed.</param>
public sealed record ToolResult(
    [property: JsonPropertyName("callId")] string CallId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("isError")] bool IsError);

/// <summary>
/// Stores a block's raw JSON as a JSON string, so it reads back as the exact text it was, even after a store that
/// normalizes JSON (such as PostgreSQL <c>jsonb</c>) has rewritten the conversation's own JSON (AGT-06).
/// </summary>
internal sealed class ContentBlockConverter : JsonConverter<ContentBlock>
{
    public override ContentBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        var raw = root.TryGetProperty("raw", out var rawElement) ? rawElement.GetString() : null;
        var call = root.TryGetProperty("toolCall", out var callElement) ? callElement.Deserialize<ToolCall>(options) : null;
        var result = root.TryGetProperty("toolResult", out var resultElement) ? resultElement.Deserialize<ToolResult>(options) : null;
        return new ContentBlock(text, raw, call, result);
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
            writer.WriteString("raw", value.Raw);
        }

        if (value.ToolCall is not null)
        {
            writer.WritePropertyName("toolCall");
            JsonSerializer.Serialize(writer, value.ToolCall, options);
        }

        if (value.ToolResult is not null)
        {
            writer.WritePropertyName("toolResult");
            JsonSerializer.Serialize(writer, value.ToolResult, options);
        }

        writer.WriteEndObject();
    }
}
