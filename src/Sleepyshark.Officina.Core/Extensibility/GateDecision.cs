using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>What a gate decides: allow, deny with a reason, ask a human, or route to another agent.</summary>
public sealed record GateDecision(PolicyAction Action, string? Reason = null, string? RouteTo = null)
{
    public static GateDecision Allow { get; } = new(PolicyAction.Allow);

    public static GateDecision Deny(string reason) => new(PolicyAction.Deny, reason);

    public static GateDecision Ask(string reason) => new(PolicyAction.Ask, reason);
}
