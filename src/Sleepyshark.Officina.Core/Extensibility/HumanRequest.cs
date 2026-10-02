using System.Text.Json;

namespace Sleepyshark.Officina.Core.Extensibility;

public enum HumanRequestKind
{
    /// <summary>A tool call waits for approval; the human may approve a changed version (HITL-02).</summary>
    Approval,

    /// <summary>The agent asks the owner a question, and the answer is text (HITL-06).</summary>
    Question,

    /// <summary>The run waits for the owner's sign-off to go on (HITL-04).</summary>
    SignOff,
}

/// <summary>Something an agent waits for a human to answer.</summary>
/// <param name="Kind">An approval, a question or a sign-off.</param>
/// <param name="Agent">The agent that waits.</param>
/// <param name="Summary">What is asked: why approval is needed, the question, or what the sign-off is for.</param>
/// <param name="Deadline">When no answer means a denial, or no answer to a question; the core applies it.</param>
/// <param name="Tool">For an approval, the tool, by its configured name.</param>
/// <param name="Arguments">For an approval, the arguments to approve.</param>
public sealed record HumanRequest(
    HumanRequestKind Kind, string Agent, string Summary, DateTimeOffset Deadline, string? Tool = null, JsonElement? Arguments = null);
