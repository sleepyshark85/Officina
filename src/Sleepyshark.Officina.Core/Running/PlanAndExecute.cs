using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// One step writes a plan, a list of <c>steps</c> in its structured output, and another carries out each item in turn
/// (PAT-01). The planner's input starts by saying it only plans. Each item's input holds the work, the plan, the outputs
/// of the items before it, each labelled (PAT-04), and then the item, so an item can build on the earlier ones. Every
/// earlier output is passed, so a long plan of long outputs makes long inputs: keeping the plan short is the planner's
/// part. When an item does not complete, the planner is asked again with what happened, up to a limit. The output is the
/// JSON list of the items' outputs.
/// </summary>
internal sealed class PlanAndExecute : ILoopPattern
{
    /// <summary>
    /// What the planner is for. It goes in the planner's input, as a team lead's does: the stable prefix is built from the
    /// agent's definition alone (CTX-02), and the planner may be an agent that has other roles.
    /// </summary>
    internal const string PlannerNote =
        "Write the plan for the work below, and do not do the work yourself: short steps, in order, that a worker carries out " +
        "one at a time. Each step's worker sees the work, the plan and the earlier steps' results.";

    public async ValueTask<StepResult> RunAsync(PatternContext context, CancellationToken ct)
    {
        var pattern = context.Pattern;
        var input = context.Input;
        for (var replans = 0; ; replans++)
        {
            var plan = await context.RunStepAsync("planner", pattern.Planner ?? new(), $"{PlannerNote}\n\n{input}", ct).ConfigureAwait(false);
            if (plan.Outcome != StepOutcome.Completed)
            {
                return plan;
            }

            if (Condition.Read(JsonElement.Parse(plan.Output), "output.steps") is not { ValueKind: JsonValueKind.Array } items)
            {
                return context.HandOff(HandoffReason.InvalidStructuredOutput, "the plan has no list of steps", plan.Output);
            }

            var outputs = new List<string>();
            StepResult? stopped = null;
            var failed = -1;
            var count = items.GetArrayLength();
            foreach (var (index, item) in items.EnumerateArray().Index())
            {
                (string, string)[] parts =
                    [("input", context.Input), ("step:planner", plan.Output), .. outputs.Select((output, earlier) => ($"step:executor[{earlier}]", output))];
                var stepInput = $"{Labels.Parts(parts)}\n\nYour step ({index + 1} of {count}):\n{Condition.Text(item)}";
                var done = await context.RunStepAsync($"executor[{index}]", pattern.Executor!, stepInput, ct).ConfigureAwait(false);
                if (done.Outcome != StepOutcome.Completed)
                {
                    stopped = done;
                    failed = index;
                    break;
                }

                outputs.Add(done.Output);
            }

            if (stopped is null)
            {
                return new(StepOutcome.Completed, JsonSerializer.Serialize(outputs));
            }

            if (stopped.Outcome == StepOutcome.Cancelled || replans == pattern.MaxReplans)
            {
                return stopped;
            }

            input = Labels.Parts(("input", context.Input), ("step:planner", plan.Output),
                ("step:failed", $"executor[{failed}] did not complete"), ("step:executor", stopped.Output));
        }
    }
}
