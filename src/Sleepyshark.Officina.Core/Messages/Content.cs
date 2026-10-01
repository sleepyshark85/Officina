using System.Collections.Immutable;
using System.Text.Json;

namespace Sleepyshark.Officina.Core.Messages;

/// <summary>One piece of a message (MSG-02).</summary>
public abstract record Content;

public sealed record TextContent : Content
{
    public TextContent(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    public string Text { get; }
}

/// <summary>An image or a document, such as a PDF, told apart by its media type.</summary>
public sealed record MediaContent : Content
{
    public MediaContent(string mediaType, ImmutableArray<byte> data)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        MediaType = mediaType;
        Data = data;
    }

    public string MediaType { get; }

    public ImmutableArray<byte> Data { get; }
}

/// <summary>The model's reasoning, kept exactly as received so it can be sent back unchanged.</summary>
/// <remarks>The signature is the provider's proof that the text is unchanged; null where the provider has none.</remarks>
public sealed record ReasoningContent : Content
{
    public ReasoningContent(string text, string? signature)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        Signature = signature;
    }

    public string Text { get; }

    public string? Signature { get; }
}

/// <summary>A tool call the model asks for. Its result answers it by <see cref="Id"/>.</summary>
public sealed record ToolUseContent : Content
{
    public ToolUseContent(string id, string name, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        Id = id;
        Name = name;
        Arguments = arguments.Clone();
    }

    public string Id { get; }

    public string Name { get; }

    public JsonElement Arguments { get; }
}

/// <summary>The result of the tool call with the id <see cref="ToolUseId"/>.</summary>
public sealed record ToolResultContent : Content
{
    public ToolResultContent(string toolUseId, string text, bool isError)
    {
        ArgumentNullException.ThrowIfNull(toolUseId);
        ArgumentNullException.ThrowIfNull(text);
        ToolUseId = toolUseId;
        Text = text;
        IsError = isError;
    }

    public string ToolUseId { get; }

    public string Text { get; }

    public bool IsError { get; }
}

/// <summary>Content of a provider's own that the core does not understand. It is carried through unchanged.</summary>
public sealed record ProviderContent : Content
{
    public ProviderContent(JsonElement data) => Data = data.Clone();

    public JsonElement Data { get; }
}
