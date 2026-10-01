namespace Sleepyshark.Officina.Core.Messages;

/// <summary>One piece of a message. Later slices add images, documents, tool requests and results, and reasoning (MSG-02).</summary>
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
