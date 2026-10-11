namespace Sleepyshark.Officina;

/// <summary>What a tool's handler returns: content for the model, and whether it is an error result.</summary>
public sealed record ToolOutput(string Content, bool IsError = false)
{
    public string Content { get; } = Content ?? throw new ArgumentNullException(nameof(Content));
}
