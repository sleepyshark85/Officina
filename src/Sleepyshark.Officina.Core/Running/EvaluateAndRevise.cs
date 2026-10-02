using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// A step produces the work, and checks decide whether it is done (PAT-01, INV-09). After a failure, the step runs again
/// with the input, its last work and the findings, masked, until the checks pass or the revisions run out (OUT-03).
/// </summary>
internal sealed class EvaluateAndRevise : ILoopPattern
{
    public async ValueTask<StepResult> RunAsync(PatternContext context, CancellationToken ct)
    {
        var pattern = context.Pattern;
        var input = context.Input;
        for (var revisions = 0; ; revisions++)
        {
            var generated = await context.RunStepAsync("generate", pattern.Generate ?? new(), input, ct).ConfigureAwait(false);
            if (generated.Outcome != StepOutcome.Completed || await context.CheckAsync(pattern.Checks, generated.Output, ct).ConfigureAwait(false) is not { } failed)
            {
                return generated;
            }

            if (revisions == pattern.MaxRevisions)
            {
                return context.HandOff(HandoffReason.OutputCheckFailed, failed, generated.Output);
            }

            input = Labels.Parts(("input", context.Input), ("step:generate", generated.Output), ("checks", failed));
        }
    }
}
