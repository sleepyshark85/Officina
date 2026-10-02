using System.Text.Json;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>A human's answer: approve, approve a changed version, deny, or text for a question.</summary>
/// <param name="Approved">Whether the request is approved; for a question, whether it is answered.</param>
/// <param name="ChangedArguments">For an approval, the changed version to run instead; it is checked again first (HITL-02).</param>
/// <param name="Text">For a question, the answer.</param>
public sealed record HumanAnswer(bool Approved, JsonElement? ChangedArguments = null, string? Text = null)
{
    public static HumanAnswer Approve { get; } = new(true);

    public static HumanAnswer Deny { get; } = new(false);

    public static HumanAnswer ApproveChanged(JsonElement arguments) => new(true, arguments);

    public static HumanAnswer Reply(string text) => new(true, Text: text ?? throw new ArgumentNullException(nameof(text)));
}
