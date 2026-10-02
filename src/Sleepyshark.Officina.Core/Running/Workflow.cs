using System.Globalization;
using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// A fixed sequence of steps (PAT-01). After each step, its outcome decides what follows (PAT-08); after a completed
/// one, the first branch rule whose condition holds on its structured output (PAT-03). A step gets the inputs it
/// declares (PAT-04). The workflow ends with the result of the last step it ran.
/// </summary>
internal sealed class Workflow : ILoopPattern
{
    public async ValueTask<StepResult> RunAsync(PatternContext context, CancellationToken ct)
    {
        var steps = context.Pattern.Steps;
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["input"] = context.Input };
        var retries = new Dictionary<string, int>(StringComparer.Ordinal);
        StepResult result = new(StepOutcome.Completed, context.Input);
        for (var index = 0; index < steps.Count;)
        {
            var step = steps[index];
            var id = step.Id!;
            var inputs = step.Input ?? ["input"];
            var input = inputs.Count == 1
                ? outputs.GetValueOrDefault(inputs[0], "")
                : Labels.Parts([.. inputs.Select(from => (from == "input" ? from : $"step:{from}", outputs.GetValueOrDefault(from, "")))]);
            result = await context.RunStepAsync(id, step, input, ct).ConfigureAwait(false);
            outputs[id] = result.Output;
            var (kind, argument) = OutcomeActions.Parse(result.Outcome switch
            {
                StepOutcome.Completed => step.OnOutcome.Completed,
                StepOutcome.HandedOff => step.OnOutcome.HandedOff,
                StepOutcome.Failed => step.OnOutcome.Failed,
                _ => "handoff", // a cancelled run ends
            });
            retries[id] = retries.GetValueOrDefault(id) + (kind == "retry" ? 1 : 0);
            var next = kind switch
            {
                "retry" when retries[id] <= int.Parse(argument!, CultureInfo.InvariantCulture) => id,
                "retry" or "handoff" => BranchRule.End,
                "goto" => argument,
                _ when result.Outcome == StepOutcome.Completed =>
                    context.Pattern.Next.FirstOrDefault(rule => rule.From == id && (rule.When?.Holds(JsonElement.Parse(result.Output)) ?? true))?.Goto,
                _ => null,
            };
            index = next is null ? index + 1 : next == BranchRule.End ? steps.Count : steps.Index().First(other => other.Item.Id == next).Index;
        }

        return result;
    }
}
