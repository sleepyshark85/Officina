using Sleepyshark.Officina.Core.Events;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// What each agent is doing, and the cost so far, from the run's events (UX-01). Each change is printed as a line as it
/// happens; <see cref="Print"/> shows everything at a glance, with what waits for the owner.
/// </summary>
/// <param name="output">Where the lines go.</param>
internal sealed class StatusView(TextWriter output)
{
    private readonly Dictionary<string, string> doing = new(StringComparer.Ordinal);
    private decimal cost;

    public void Apply(CoreEvent coreEvent)
    {
        ArgumentNullException.ThrowIfNull(coreEvent);
        string? now = coreEvent.Payload switch
        {
            TurnStarted => "working",
            ToolCallStarted started => $"running {started.Tool}",
            ToolCallEnded { Error: { } error } ended => $"{ended.Tool} failed: {error}",
            HumanAsked asked => $"waits for you: {asked.Request} {asked.Tool}".TrimEnd(),
            HumanAnswered { TimedOut: true } => "working; nobody answered in time",
            HumanAnswered => "working",
            TurnEnded ended => $"ended: {ended.Outcome}{(ended.Reason is { } reason ? $" ({reason})" : "")}",
            _ => null,
        };
        lock (doing)
        {
            if (coreEvent.Payload is ModelCallEnded call)
            {
                cost += call.Cost;
                output.WriteLine($"[{coreEvent.Agent}] model call: {call.Usage.Total} tokens, ${call.Cost:0.00}; cost so far ${cost:0.00}");
            }

            if (now is not null)
            {
                doing[coreEvent.Agent] = now;
                output.WriteLine($"[{coreEvent.Agent}] {now}");
            }
        }
    }

    /// <summary>Every agent's status, what waits for the owner, and the cost so far.</summary>
    public void Print(OwnerQueue queue)
    {
        lock (doing)
        {
            foreach (var (agent, now) in doing.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                output.WriteLine($"{agent}: {now}");
            }

            foreach (var (number, request) in queue.Waiting)
            {
                output.WriteLine($"waiting for you: #{number} {OwnerQueue.Describe(request)}");
            }

            output.WriteLine($"cost so far: ${cost:0.00}");
        }
    }
}
