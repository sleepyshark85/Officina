using System.Globalization;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Workspace;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// What each agent is doing, and the cost so far, from the events it is given (UX-01). Each change is printed as a line as it
/// happens; <see cref="Print"/> shows everything at a glance, with what waits for the owner. One view can follow several
/// runs one after another, as in <c>sof chat</c>, and its cost is then theirs together.
/// </summary>
/// <param name="output">Where the lines go.</param>
/// <param name="queue">The integration queue's length and waiting time, when the workspace is on (WS-09).</param>
/// <param name="stream">Whether the model's text is printed as it is generated (LAT-02), each agent's after its name.</param>
internal sealed class StatusView(TextWriter output, Func<IntegrationQueueStatus?>? queue = null, bool stream = false)
{
    private readonly Dictionary<string, string> doing = new(StringComparer.Ordinal);
    private decimal cost;

    /// <summary>The agent whose text was printed last, until a line ends it.</summary>
    private string? streaming;

    /// <summary>Whether the last thing printed is text that has not ended its line.</summary>
    private bool midLine;

    /// <summary>The cost of the events applied so far.</summary>
    public decimal Cost
    {
        get
        {
            lock (doing)
            {
                return cost;
            }
        }
    }

    public void Apply(CoreEvent coreEvent)
    {
        ArgumentNullException.ThrowIfNull(coreEvent);
        string? now = coreEvent.Payload switch
        {
            TurnStarted => "working",
            ToolCallStarted started => $"running {started.Tool}",
            ToolCallEnded { Error: { } error } ended => $"{ended.Tool} failed: {error}",
            BudgetWarning warning => $"the {warning.Level} {warning.Limit} budget is {warning.Used:P0} used",
            HumanAsked asked => $"waits for you: {asked.Request} {asked.Tool}".TrimEnd(),
            HumanAnswered { TimedOut: true } => "working; nobody answered in time",
            HumanAnswered => "working",
            TurnEnded ended => $"ended: {ended.Outcome}{(ended.Reason is { } reason ? $" ({reason})" : "")}",
            AgentStatusChanged changed => $"{changed.Status.ToString().ToLowerInvariant()}{(changed.Detail is { } detail ? $": {detail}" : "")}",
            MessageSent sent => $"sent {sent.To} a message",
            Warning warning => $"warning: {warning.Text}",
            ModelCallRetried retried when stream => $"the model call is tried again ({retried.Failure}), so its text above is void",
            _ => null,
        };
        lock (doing)
        {
            if (coreEvent.Payload is TextGenerated text && stream)
            {
                if (streaming != coreEvent.Agent)
                {
                    EndLine();
                    output.Write($"[{coreEvent.Agent}] ");
                    streaming = coreEvent.Agent;
                }

                output.Write(text.Text);
                midLine = !text.Text.EndsWith('\n');
            }

            if (coreEvent.Payload is ModelCallEnded call)
            {
                cost += call.Cost;
                // Costs are written the same in every culture, so whatever reads the console reads them alike.
                Line(string.Create(CultureInfo.InvariantCulture, $"[{coreEvent.Agent}] model call: {call.Usage.Total} tokens, ${call.Cost:0.00}; cost so far ${cost:0.00}"));
            }

            if (now is not null)
            {
                doing[coreEvent.Agent] = now;
                Line($"[{coreEvent.Agent}] {now}");
            }
        }
    }

    /// <summary>Every agent's status, what waits for the owner, and the cost so far.</summary>
    public void Print(OwnerQueue owner)
    {
        lock (doing)
        {
            foreach (var (agent, now) in doing.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                Line($"{agent}: {now}");
            }

            foreach (var (number, request) in owner.Waiting)
            {
                Line($"waiting for you: #{number} {owner.Describe(number, request)}");
            }

            if (queue?.Invoke() is { } integration)
            {
                Line($"integration queue: {integration.Length} waiting, longest wait {integration.LongestWait:hh\\:mm\\:ss}");
            }

            Line(string.Create(CultureInfo.InvariantCulture, $"cost so far: ${cost:0.00}"));
        }
    }

    /// <summary>Prints a line of its own, after any text still on the line.</summary>
    public void WriteLine(string line)
    {
        lock (doing)
        {
            Line(line);
        }
    }

    /// <summary>A line of its own, after any text still on the line.</summary>
    private void Line(string line)
    {
        EndLine();
        streaming = null;
        output.WriteLine(line);
    }

    private void EndLine()
    {
        if (midLine)
        {
            output.WriteLine();
            midLine = false;
        }
    }
}
