using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// A loop pattern (PAT-01): a built-in one, or the application's, which configuration selects by name with
/// <c>extension:&lt;id&gt;</c> (PAT-07). It runs its steps only through <see cref="PatternContext"/>, so they obey the
/// same budgets, cancellation, events and handoffs as every other step (PAT-06).
/// </summary>
public interface ILoopPattern
{
    ValueTask<StepResult> RunAsync(PatternContext context, CancellationToken ct);
}

/// <summary>How a step ended (PAT-08).</summary>
public enum StepOutcome
{
    Completed,
    HandedOff,
    Failed,
    Cancelled,
}

/// <summary>What a step reports (PAT-08).</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Output">Its output when completed; otherwise why it was not.</param>
/// <param name="Handoff">The handoff, when it was handed off or cancelled.</param>
public sealed record StepResult(StepOutcome Outcome, string Output, Handoff? Handoff = null);

/// <summary>What a pattern runs with: its input, its configuration, and the core's step runner (PAT-06).</summary>
public sealed class PatternContext
{
    private readonly Steps steps;
    private readonly ToolContext context;
    private readonly Budget budget;

    internal PatternContext(Steps steps, ToolContext context, PatternOptions pattern, string input, Budget budget)
    {
        this.steps = steps;
        this.context = context;
        this.budget = budget;
        Pattern = pattern;
        Input = input;
    }

    /// <summary>The pattern's configuration; an application's pattern reads the settings it needs from it.</summary>
    public PatternOptions Pattern { get; }

    /// <summary>What the pattern is given to work on.</summary>
    public string Input { get; }

    /// <summary>
    /// Runs a step, which draws on the pattern's budget and always reports how it ended. Its events and traces name it
    /// by its id, after the ids of the steps it is nested in.
    /// </summary>
    /// <param name="id">The step's name in this pattern.</param>
    /// <param name="step">What the step is: a turn of an agent, the agent's own pattern, or a nested pattern.</param>
    /// <param name="input">All that the step gets (PAT-04).</param>
    /// <param name="ct">Cancels the step, which then reports <see cref="StepOutcome.Cancelled"/>.</param>
    public Task<StepResult> RunStepAsync(string id, StepOptions step, string input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(input);
        return steps.RunStepAsync(context with { Step = context.Step is null ? id : $"{context.Step}/{id}" }, step, input, budget, ct);
    }

    /// <summary>Ends the pattern in a handoff, built from recorded state only (EGR-02).</summary>
    /// <param name="reason">Why.</param>
    /// <param name="detail">What happened, for the receiver.</param>
    /// <param name="lastText">The last output the pattern had.</param>
    public StepResult HandOff(HandoffReason reason, string detail, string lastText = "") =>
        new(StepOutcome.HandedOff, detail, new Handoff(reason, null, detail, steps.RunInput, [], null, lastText));

    /// <summary>The first of the named checks the output fails, and its findings, masked; null when it passes them all.</summary>
    internal Task<string?> CheckAsync(IReadOnlyList<string> checks, string output, CancellationToken ct) => steps.CheckAsync(context, checks, output, ct);
}
