using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>Why a turn was handed off (EGR-03). Self-reported confidence and sentiment are never reasons.</summary>
public enum HandoffReason
{
    RequestedByHuman,
    PolicyGap,
    NoProgress,
    BudgetExhausted,
    ProviderRefusal,
    ProviderFailure,
    TruncatedOutput,
    InvalidStructuredOutput,
    ApprovalDeniedOrTimedOut,
    RoutedByGate,
    OutputCheckFailed,
    VerificationFailed,
    NoRouteForValue,
    PassedToNextAgent,
}

/// <summary>
/// Work handed on, built entirely from recorded state; no model is asked to summarise (EGR-02). A handoff goes to a
/// human only on an explicit signal, such as a gate's route, never because of words in the model's text (EGR-04).
/// Its result carries the run record's facts, findings, decisions and citations.
/// </summary>
/// <param name="Reason">Why.</param>
/// <param name="To">An agent, <see cref="ToolResult.Human"/>, or null for the step that started the work.</param>
/// <param name="Detail">What happened, for the receiver; never internal details or secrets.</param>
/// <param name="Work">The original work.</param>
/// <param name="ToolCalls">The tool calls attempted, and their outcomes, in order.</param>
/// <param name="PendingAction">The call that was routed and not run, if any.</param>
/// <param name="LastText">The agent's last text.</param>
public sealed record Handoff(
    HandoffReason Reason, string? To, string Detail, string Work, IReadOnlyList<ToolAttempt> ToolCalls, ToolRequest? PendingAction, string LastText);

/// <summary>A tool call and its outcome.</summary>
public sealed record ToolAttempt(ToolRequest Request, ToolResult Result);
