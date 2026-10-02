using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// One step writes a plan, a list of <c>steps</c> in its structured output, and another carries out each item in turn
/// (PAT-01). When an item does not complete, the planner is asked again with what happened, up to a limit. The output
/// is the JSON list of the items' outputs.
/// </summary>
internal sealed class PlanAndExecute : ILoopPattern
{
    public async ValueTask<StepResult> RunAsync(PatternContext context, CancellationToken ct)
    {
        var pattern = context.Pattern;
        var input = context.Input;
        for (var replans = 0; ; replans++)
        {
            var plan = await context.RunStepAsync("planner", pattern.Planner ?? new(), input, ct).ConfigureAwait(false);
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
            foreach (var (index, item) in items.EnumerateArray().Index())
            {
                var done = await context.RunStepAsync($"executor[{index}]", pattern.Executor!, Condition.Text(item), ct).ConfigureAwait(false);
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
