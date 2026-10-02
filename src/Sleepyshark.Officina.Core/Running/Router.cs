using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// A step classifies the input, and the route for the value of a field of its structured output gets the input
/// (PAT-01). A value without a route goes to <c>otherwise</c>, or ends in a handoff, never a guess (PAT-03).
/// </summary>
internal sealed class Router : ILoopPattern
{
    public async ValueTask<StepResult> RunAsync(PatternContext context, CancellationToken ct)
    {
        var pattern = context.Pattern;
        var classified = await context.RunStepAsync("classify", pattern.Classify ?? new(), context.Input, ct).ConfigureAwait(false);
        if (classified.Outcome != StepOutcome.Completed)
        {
            return classified;
        }

        var value = Condition.Read(JsonElement.Parse(classified.Output), pattern.On!) is { } found ? Condition.Text(found) : null;
        var route = value is not null && pattern.Routes.ContainsKey(value) ? value : pattern.Otherwise;
        return route is null
            ? context.HandOff(HandoffReason.NoRouteForValue, $"no route for {pattern.On} {value ?? "(missing)"}", classified.Output)
            : await context.RunStepAsync(route, pattern.Routes[route], context.Input, ct).ConfigureAwait(false);
    }
}
