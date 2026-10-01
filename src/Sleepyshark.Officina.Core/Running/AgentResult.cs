using System.Collections.Immutable;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Core.Running;

public enum AgentOutcome
{
    Completed,
    HandedOff,
    Rejected,

    /// <summary>Something inside the core or an extension threw; the run ended without a crash (REL-02).</summary>
    Failed,
}

/// <summary>How a run ended (EGR-01). Every run ends in one of these, never a crash (INV-07, REL-02).</summary>
/// <param name="Outcome">Whether the run completed, was handed off, was rejected or failed.</param>
/// <param name="Output">The agent's output when completed; otherwise why it was not.</param>
/// <param name="Statistics">What the turn used.</param>
/// <param name="Transcript">The conversation, in which every tool request has its result (LOOP-09).</param>
/// <param name="CacheWarnings">The model calls that read too little of their input from the cache (COST-01).</param>
/// <param name="Handoff">The handoff, when handed off. With this result's statistics and transcript it is everything EGR-02 lists.</param>
public sealed record AgentResult(
    AgentOutcome Outcome,
    string Output,
    TurnStatistics Statistics,
    ImmutableArray<Message> Transcript,
    ImmutableArray<CacheWarning> CacheWarnings,
    Handoff? Handoff = null);

/// <summary>A model call that read less of its input from the cache than <c>context.cacheHitWarning</c> (COST-01).</summary>
/// <param name="Iteration">Which call of the turn, from 1.</param>
/// <param name="HitRate">The share of the call's input read from the cache.</param>
public sealed record CacheWarning(int Iteration, double HitRate);

/// <summary>What a turn used (EGR-01).</summary>
/// <param name="Iterations">Model calls.</param>
/// <param name="ToolCalls">Tool calls, including tools the provider ran itself (TOOL-13).</param>
/// <param name="Usage">Tokens, by kind (MSG-06).</param>
/// <param name="Cost">Cost in USD, from the configured prices (MDL-09).</param>
/// <param name="Elapsed">Time since the turn started.</param>
public sealed record TurnStatistics(int Iterations, int ToolCalls, Usage Usage, decimal Cost, TimeSpan Elapsed);
