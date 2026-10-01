namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// A rule that needs code, run before each call of the tools it is attached to (TOOL-05). It must be deterministic:
/// no model calls and no side effects.
/// </summary>
public interface IGate
{
    ValueTask<GateDecision> EvaluateAsync(GateContext context, CancellationToken ct);
}
