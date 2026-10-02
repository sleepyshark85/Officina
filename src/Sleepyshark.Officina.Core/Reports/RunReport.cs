using System.Globalization;
using System.Text;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tasks;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Reports;

/// <summary>What was spent on model calls: in USD, in tokens, and in calls.</summary>
public sealed record Spend(decimal Cost, long Tokens, int Calls);

/// <summary>
/// What a run cost, in total and by agent, definition, task, step and model (RUN-10). Work that is for no task, or not in a pattern's
/// step, is under <see cref="None"/>. An agent of a team is its instance, such as <c>developer[2]</c>, and its definition is <c>developer</c>.
/// </summary>
public sealed record CostBreakdown(
    Spend Total,
    IReadOnlyDictionary<string, Spend> ByAgent,
    IReadOnlyDictionary<string, Spend> ByDefinition,
    IReadOnlyDictionary<string, Spend> ByTask,
    IReadOnlyDictionary<string, Spend> ByStep,
    IReadOnlyDictionary<string, Spend> ByModel)
{
    /// <summary>The key of work that is for no task, no step, or a model the event does not name.</summary>
    public const string None = "(none)";

    /// <summary>The breakdown of the model calls among a run's events, which is also its cost so far while the run goes on.</summary>
    public static CostBreakdown Of(IEnumerable<CoreEvent> events)
    {
        var calls = events.Where(coreEvent => coreEvent.Payload is ModelCallEnded).Select(coreEvent => (Event: coreEvent, Call: (ModelCallEnded)coreEvent.Payload)).ToList();
        return new(Sum(calls), By(calls, call => call.Event.Agent), By(calls, call => ToolContext.DefinitionOf(call.Event.Agent)), By(calls, call => call.Call.Task), By(calls, call => call.Event.Step), By(calls, call => call.Call.Model));

        static Spend Sum(IEnumerable<(CoreEvent Event, ModelCallEnded Call)> calls) =>
            new(calls.Sum(call => call.Call.Cost), calls.Sum(call => (long)call.Call.Usage.Total), calls.Count());

        static Dictionary<string, Spend> By(IEnumerable<(CoreEvent Event, ModelCallEnded Call)> calls, Func<(CoreEvent Event, ModelCallEnded Call), string?> key) =>
            calls.GroupBy(call => key(call) ?? None, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, Sum, StringComparer.Ordinal);
    }
}

/// <summary>A task as the run left it.</summary>
public sealed record ReportedTask(string Id, string Title, TaskState State, decimal Spent, decimal Budget);

/// <summary>A check that ran, and how often it passed and failed.</summary>
public sealed record ReportedCheck(string Check, string? Task, int Passed, int Failed);

/// <summary>
/// The report of a run that has ended or stopped for a human: the outcome, the work done, the decisions made, the checks, the
/// cost and what is still open (RUN-11). It is built from what is stored, so it can be made at any time, also after a crash.
/// </summary>
public sealed record RunReport(
    string RunId,
    string Agent,
    string Input,
    DateTimeOffset Started,
    RunStatus Status,
    string Outcome,
    TimeSpan Running,
    int Resumes,
    CostBreakdown Cost,
    IReadOnlyList<ReportedTask> Tasks,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<ReportedCheck> Checks,
    IReadOnlyList<string> OpenIssues)
{
    /// <summary>Builds the report of a stored run; null when the tenant has no such run.</summary>
    public static async Task<RunReport?> BuildAsync(IStorage storage, string? tenant, string runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (await storage.Runs.ReadAsync(tenant, runId, ct).ConfigureAwait(false) is not { } stored)
        {
            return null;
        }

        var events = await storage.Events.ReadAsync(tenant, runId, 0, ct).ConfigureAwait(false);
        var record = await storage.Records.ReadAsync(tenant, runId, ct).ConfigureAwait(false);
        var tasks = (await storage.Tasks.ReadAsync(tenant, runId, ct).ConfigureAwait(false)).SelectMany(change => change.Tasks)
            .GroupBy(task => task.Id, StringComparer.Ordinal).Select(group => group.Last())
            .Select(task => new ReportedTask(task.Id, task.Title, task.State, task.Spent, task.Budget)).ToList();
        var ended = events.Select(coreEvent => coreEvent.Payload).OfType<TurnEnded>().LastOrDefault();
        var outcome = ended is null ? "did not end" : $"{ended.Outcome}{(ended.Reason is { } reason ? $" ({reason})" : "")}";
        var checks = events.Select(coreEvent => coreEvent.Payload).OfType<CheckRan>().GroupBy(check => (check.Check, check.Task))
            .Select(group => new ReportedCheck(group.Key.Check, group.Key.Task, group.Count(check => check.Passed), group.Count(check => !check.Passed))).ToList();
        var issues = new List<string>();
        if (stored.Status == RunStatus.Running)
        {
            issues.Add("The run did not end: its process died, or it is still going. Continue it with a resume.");
        }
        else if (ended is { Outcome: AgentOutcome.HandedOff })
        {
            issues.Add($"The agent handed off: {outcome}.");
        }

        issues.AddRange(tasks.Where(task => task.State is not (TaskState.Done or TaskState.Cancelled)).Select(task => $"Task {task.Id} ({task.Title}) is {task.State}."));
        issues.AddRange(record.Where(entry => record.Any(other => RunRecord.Conflict(entry, other, record))).Select(entry => RunRecord.Describe(entry, record)));
        issues.AddRange(checks.Where(check => check.Failed > 0 && check.Passed == 0).Select(check => $"Check {check.Check}{(check.Task is null ? "" : $" on task {check.Task}")} never passed."));
        return new(
            runId, stored.Started.Agent, stored.Started.Input, stored.Started.Time, stored.Status, outcome, Spent.Of(events).Run.Time,
            events.Count(coreEvent => coreEvent.Payload is RunResumed), CostBreakdown.Of(events), tasks,
            [.. record.Where(entry => entry.Item is Decision).Select(entry => RunRecord.Describe(entry, record))], checks, issues);
    }

    /// <summary>The report as text, for the owner to read.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Run {RunId}: {Status}, {Outcome}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Agent {Agent}, started {Started:u}, running {(int)Running.TotalHours}:{Running:mm\\:ss}{(Resumes > 0 ? $", resumed {Resumes} times" : "")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Work: {Input}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Cost: ${Cost.Total.Cost:0.00} in {Cost.Total.Calls} model calls, {Cost.Total.Tokens} tokens");
        foreach (var (name, by) in new[] { ("agent", Cost.ByAgent), ("definition", Cost.ByDefinition), ("task", Cost.ByTask), ("step", Cost.ByStep), ("model", Cost.ByModel) })
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  by {name}: {string.Join(", ", by.Select(line => $"{line.Key} ${line.Value.Cost:0.00}"))}");
        }

        Section(text, "Tasks", Tasks.Select(task => $"{task.Id} {task.Title}: {task.State}, ${task.Spent:0.00} of ${task.Budget:0.00}"));
        Section(text, "Decisions", Decisions);
        Section(text, "Checks", Checks.Select(check => $"{check.Check}{(check.Task is null ? "" : $" on {check.Task}")}: {check.Passed} passed, {check.Failed} failed"));
        Section(text, "Open", OpenIssues);
        return text.ToString();

        static void Section(StringBuilder text, string title, IEnumerable<string> lines)
        {
            if (lines.Any())
            {
                text.AppendLine(title + ":");
                foreach (var line in lines)
                {
                    text.AppendLine("  " + line);
                }
            }
        }
    }
}
