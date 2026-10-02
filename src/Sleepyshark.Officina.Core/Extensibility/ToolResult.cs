using System.Collections.Immutable;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// The result of a tool call: content with any artifacts, an error, or a route to another agent. An error carries only
/// its category and a short message, never internal details (TOOL-08).
/// </summary>
public sealed record ToolResult
{
    private ToolResult(string content, ToolErrorCategory? error, string? routeTo)
    {
        Content = content;
        Error = error;
        RouteTo = routeTo;
    }

    /// <summary>What the model reads: the result, or the error's category and short message.</summary>
    public string Content { get; internal init; }

    /// <summary>Files, reports or data the call produced. The turn's result carries them (OUT-05); the model does not read them.</summary>
    public ImmutableArray<Artifact> Artifacts { get; internal init; } = [];

    public ToolErrorCategory? Error { get; }

    /// <summary>Whether trying the call again may help.</summary>
    public bool Retryable => Error is ToolErrorCategory.Timeout or ToolErrorCategory.Unavailable;

    /// <summary>The agent the call was routed to, or <see cref="Human"/>; the turn ends in a handoff to it.</summary>
    public string? RouteTo { get; }

    /// <summary>Whether the content comes from outside the agents' control, such as a check's findings on code they wrote (SEC-04).</summary>
    internal bool Untrusted { get; init; }

    /// <summary>The <see cref="RouteTo"/> of a call that goes to a human.</summary>
    public const string Human = "human";

    public static ToolResult Success(string content, params IEnumerable<Artifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new(content, null, null) { Artifacts = [.. artifacts] };
    }

    /// <param name="category">Why the call did not succeed.</param>
    /// <param name="reason">A short reason the model may read, such as a configured denial reason; never internal details.</param>
    public static ToolResult Failed(ToolErrorCategory category, string? reason = null)
    {
        var message = category switch
        {
            ToolErrorCategory.UnknownTool => "unknown tool",
            ToolErrorCategory.InvalidArguments => "invalid arguments",
            ToolErrorCategory.NotAuthorised => "not authorised",
            ToolErrorCategory.PolicyViolation => "policy violation",
            ToolErrorCategory.ApprovalDenied => "approval denied",
            ToolErrorCategory.Timeout => "timed out",
            ToolErrorCategory.Cancelled => "cancelled",
            ToolErrorCategory.Unavailable => "unavailable",
            _ => "failed",
        };
        return new(reason is null ? message : $"{message}: {reason}", category, null);
    }

    public static ToolResult Routed(string to, string reason) => new($"routed to {to}: {reason}", null, to);
}
