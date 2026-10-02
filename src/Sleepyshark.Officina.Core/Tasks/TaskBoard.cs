using System.Text.Json;
using System.Text.Json.Serialization;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Observability;
using Sleepyshark.Officina.Core.Tools;
using static Sleepyshark.Officina.Core.Tasks.TaskState;

namespace Sleepyshark.Officina.Core.Tasks;

/// <summary>
/// A run's task board, as one agent or the owner acts on it (TASK). Every change goes through one step, which applies it
/// to the board as it is, checks the rules on the result, and appends it with the next revision. When another change
/// takes that revision first, it applies and checks again against the board as it is then, as the run record does
/// (DESIGN.md §2), so no change overwrites another and two agents never both claim a task (TASK-04). Gates and tools
/// read it (TOOL-06); agents change it through the <c>tasks.*</c> tools.
/// </summary>
public sealed class TaskBoard
{
    /// <summary>Who the owner's changes are attributed to.</summary>
    public const string Owner = "owner";

    private const string HostOnly = "only the owner or the host completes or returns a task.";

    /// <summary>The status changes allowed (TASK-02, DESIGN.md §6). The operations below make each one.</summary>
    private static readonly HashSet<(TaskState From, TaskState To)> Transitions =
    [
        (Proposed, Ready), (Ready, Proposed), // its dependencies are done, or no longer all done (TASK-03)
        (Ready, InProgress), // claimed
        (InProgress, InReview), // its checks passed (TASK-05)
        (InReview, Done), // reviewed, if it requires a review, and integrated
        (InReview, InProgress), // changes asked, or returned by integration (WS-03)
        (InProgress, Blocked), (Blocked, Ready), (Blocked, Proposed),
        (InProgress, Failed), (InReview, Failed), // out of attempts or budget (TASK-09)
        (Failed, Ready), (Failed, Proposed), // retried
        .. new[] { Proposed, Ready, InProgress, InReview, Blocked, Failed }.Select(open => (open, Cancelled)),
    ];

    private readonly ITaskStore store;
    private readonly ToolContext context;
    private readonly OfficinaOptions options;
    private readonly EventBus events;
    private readonly TimeProvider time;
    private readonly bool owner;

    /// <param name="store">Where the board is kept.</param>
    /// <param name="context">The run, and the agent its changes are attributed to.</param>
    /// <param name="options">The configuration: the board's settings and the checks tasks may name.</param>
    /// <param name="events">Where status changes are published (EVT-01).</param>
    /// <param name="time">The clock for change times.</param>
    /// <param name="owner">Whether the board acts for the owner, who may change anything at any time (TASK-08).</param>
    internal TaskBoard(ITaskStore store, ToolContext context, OfficinaOptions options, EventBus events, TimeProvider time, bool owner = false)
    {
        this.store = store;
        this.context = context;
        this.options = options;
        this.events = events;
        this.time = time;
        this.owner = owner;
    }

    public string RunId => context.RunId;

    /// <summary>How the result of a submit that a check failed begins, before the check's name and findings.</summary>
    internal const string CheckFailed = "check ";

    /// <summary>The folder of a task's working copy, opened for its agent, which its verification checks look at; null without a workspace.</summary>
    internal Func<string, string, CancellationToken, Task<string?>>? CopyOf { get; init; }

    /// <summary>The task the agent works on, if any.</summary>
    public string? TaskId => context.TaskId;

    /// <summary>How tasks are read from the tools' arguments, and written to compare and describe them.</summary>
    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The tasks as they are now, the highest priority first.</summary>
    public async ValueTask<IReadOnlyList<BoardTask>> ReadAsync(CancellationToken ct) => Current(await HistoryAsync(ct).ConfigureAwait(false));

    /// <summary>The tasks as the changes left them, the highest priority first.</summary>
    internal static IReadOnlyList<BoardTask> Current(IReadOnlyList<TaskChange> history) =>
        [.. Tasks(history).Values.OrderByDescending(task => task.Priority).ThenBy(task => task.Id, StringComparer.Ordinal)];

    /// <summary>Every change of the board in revision order: who made it, when, what changed and why (TASK-07).</summary>
    public ValueTask<IReadOnlyList<TaskChange>> HistoryAsync(CancellationToken ct) => store.ReadAsync(context.Caller.Tenant, RunId, ct);

    /// <summary>Adds a task, which is ready once its dependencies are done (TASK-03). Only the owner sets its budget or assignee.</summary>
    /// <returns>Whether it was added, and what changed or why not.</returns>
    public Task<(bool Accepted, string Text)> AddAsync(string id, TaskEdit task, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        return ChangeAsync(reason, tasks =>
        {
            if (tasks.ContainsKey(id))
            {
                return $"the board already has a task {id}.";
            }

            if (!owner && (task.Budget is not null || task.Assignee is not null))
            {
                return "only the owner sets a task's budget or assignee.";
            }

            tasks[id] = Apply(new BoardTask { Id = id, Title = task.Title ?? "", Budget = options.Capabilities.TaskBoard.Budget }, task);
            return string.IsNullOrWhiteSpace(task.Title) ? "a task needs a title." : null;
        }, ct);
    }

    /// <summary>
    /// Changes a task's fields, and blocks, unblocks, retries or cancels it. A retried task starts again with no failed
    /// attempts and nothing spent. The owner may change anything (TASK-08). No agent changes a task's checks, budget or
    /// review requirement (INV-10). The team's lead also assigns, retries and cancels tasks (TEAM-02, TEAM-09). Any other
    /// agent changes only a task nobody has claimed, and of its own claimed task only whether it is blocked: once claimed,
    /// a task is the work it is verified and reviewed against.
    /// </summary>
    public Task<(bool Accepted, string Text)> EditAsync(string id, TaskEdit edit, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return ChangeAsync(reason, tasks =>
        {
            if (!tasks.TryGetValue(id, out var task))
            {
                return NoTask(id);
            }

            if (Refused(task, edit) is { } refused)
            {
                return refused;
            }

            if (edit.State is { } state && state is not (Blocked or Ready or Cancelled))
            {
                return $"an edit cannot set a task {state}; only blocked, ready or cancelled.";
            }

            task = Apply(task, edit);
            tasks[id] = edit.State switch
            {
                null => task,
                Ready when task.State == Failed => task with { State = Ready, FailedAttempts = 0, Spent = 0 },
                _ => task with { State = edit.State.Value },
            };
            return null;
        }, ct);
    }

    /// <summary>
    /// Marks a task done once its change is integrated (WS-09). Only a task whose checks passed, and that a reviewer
    /// approved if it requires a review, can be (TASK-05).
    /// </summary>
    public Task<(bool Accepted, string Text)> CompleteAsync(string id, string reason, CancellationToken ct) =>
        ChangeAsync(reason, tasks =>
        {
            if (!owner || !tasks.TryGetValue(id, out var task))
            {
                return owner ? NoTask(id) : HostOnly;
            }

            tasks[id] = task with { State = Done };
            return null;
        }, ct);

    /// <summary>
    /// Sends a task in review back to its author, such as when its change conflicts or fails the baseline checks on
    /// integration (WS-03). It counts as a failed attempt, so the last one sends it back to the lead (TASK-09).
    /// </summary>
    public Task<(bool Accepted, string Text)> ReturnAsync(string id, string reason, CancellationToken ct) =>
        ChangeAsync(reason, tasks =>
        {
            if (!owner || !tasks.TryGetValue(id, out var task) || task.State != InReview)
            {
                return owner ? $"task {id} is not in review." : HostOnly;
            }

            tasks[id] = Rework(task);
            return null;
        }, ct);

    /// <summary>
    /// Sends a task back to the lead, failed, when the agent working on it or reviewing it stopped without finishing: it
    /// failed, stalled, ran out of budget or was stopped (TEAM-09). The reason says which.
    /// </summary>
    public Task<(bool Accepted, string Text)> FailAsync(string id, string reason, CancellationToken ct) =>
        ChangeAsync(reason, tasks =>
        {
            if (!owner || !tasks.TryGetValue(id, out var task) || task.State is not (InProgress or InReview))
            {
                return owner ? $"task {id} is not in progress or in review." : HostOnly;
            }

            tasks[id] = task with { State = Failed, Assignee = null, Verified = false, Approved = false };
            return null;
        }, ct);

    /// <summary>Takes a ready task for the agent, unless it is assigned to another (TASK-03, TASK-04).</summary>
    internal Task<(bool Accepted, string Text)> ClaimAsync(string id, CancellationToken ct) =>
        ChangeAsync("claimed", tasks =>
        {
            if (!tasks.TryGetValue(id, out var task))
            {
                return NoTask(id);
            }

            if (task.State != Ready)
            {
                return $"task {id} is {task.State}; only a Ready task, whose dependencies are done, can be claimed.";
            }

            if (task.Assignee is { } assignee && assignee != context.AgentId)
            {
                return $"task {id} is assigned to {assignee}.";
            }

            tasks[id] = task with { State = InProgress, Assignee = context.AgentId };
            return null;
        }, ct);

    /// <summary>
    /// Runs the checks of the agent's task in order, where the first that fails decides. The task is in review only if
    /// they all pass, whatever the agent says (TASK-05, INV-09); otherwise the attempt has failed.
    /// </summary>
    internal async Task<(bool Accepted, string Text)> SubmitAsync(string id, IReadOnlyList<string> artifacts, IReadOnlyDictionary<string, ICheck> checks, CancellationToken ct)
    {
        var task = (await ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(task => task.Id == id);
        if (task?.State != InProgress || task.Assignee != context.AgentId)
        {
            return (false, $"task {id} is not in progress with {context.AgentId}.");
        }

        string? failed = null;
        var directory = task.Checks.Count > 0 && CopyOf is not null ? await CopyOf(id, task.Assignee, ct).ConfigureAwait(false) : null;
        foreach (var name in task.Checks)
        {
            if (!options.Checks.ContainsKey(name))
            {
                return (false, $"check {name} no longer exists in the configuration.");
            }

            var result = await checks[options.Checks[name].Id(name)].RunAsync(new CheckContext(directory, null, [], task), ct).ConfigureAwait(false);
            Telemetry.CheckEnded(context, name, result.Passed);
            await events.PublishAsync(context, new CheckRan(name, result.Passed, id), ct).ConfigureAwait(false);
            if (!result.Passed)
            {
                failed = $"{CheckFailed}{name} failed: {string.Join("; ", result.Findings)}";
                break;
            }
        }

        var (accepted, text) = await ChangeAsync(failed ?? "its checks passed", tasks =>
        {
            if (!Same(tasks[id], task))
            {
                return $"task {id} changed while its checks ran. Submit it again.";
            }

            tasks[id] = failed is null ? task with { State = InReview, Verified = true, Artifacts = artifacts } : Rework(task);
            return null;
        }, ct).ConfigureAwait(false);
        return (accepted, failed is null || !accepted ? text : $"{failed}. {text}");
    }

    /// <summary>Records a review's outcome with its reasons. The reviewer is never the author (TASK-06).</summary>
    internal Task<(bool Accepted, string Text)> ReviewAsync(string id, bool approved, string reasons, CancellationToken ct) =>
        ChangeAsync($"{(approved ? "approved" : "changes asked")}: {reasons}", tasks =>
        {
            if (!tasks.TryGetValue(id, out var task) || task.State != InReview || !task.RequiresReview || task.Approved)
            {
                return $"task {id} is not waiting for a review.";
            }

            if (task.Assignee == context.AgentId)
            {
                return "the reviewer is never the author.";
            }

            tasks[id] = approved ? task with { Approved = true } : Rework(task);
            return null;
        }, ct);

    /// <summary>Adds the cost of a turn to the agent's task. Once its budget is used up, the task goes back to the lead (TASK-09).</summary>
    internal Task<(bool Accepted, string Text)> ChargeAsync(decimal cost, CancellationToken ct) =>
        ChangeAsync($"a turn on it cost {cost} USD", tasks =>
        {
            if (TaskId is null || !tasks.TryGetValue(TaskId, out var task))
            {
                return NoTask(TaskId);
            }

            task = task with { Spent = task.Spent + cost };
            tasks[TaskId] = task.Spent >= task.Budget && task.State is InProgress or InReview ? task with { State = Failed, Assignee = null } : task;
            return null;
        }, ct);

    /// <summary>A task as the agent working on it sees it in the volatile context (CTX-01): its status and acceptance criteria.</summary>
    internal static string Describe(BoardTask task) =>
        $"{task.Id} {task.Title}: {task.State}" + string.Concat(task.AcceptanceCriteria.Select((criterion, index) => $"{(index == 0 ? "\nAcceptance criteria:" : "")}\n- {criterion}"));

    /// <summary>
    /// Applies a change to the board as it is, makes ready the tasks whose dependencies are done, checks the rules on
    /// the result, and appends it as the next revision; on a taken revision, it starts again.
    /// </summary>
    /// <param name="reason">Why, as recorded (TASK-07).</param>
    /// <param name="change">Changes the tasks, by id, and returns why it cannot, or null.</param>
    /// <param name="ct">Cancels the change.</param>
    private async Task<(bool Accepted, string Text)> ChangeAsync(string reason, Func<Dictionary<string, BoardTask>, string?> change, CancellationToken ct)
    {
        while (true)
        {
            var history = await HistoryAsync(ct).ConfigureAwait(false);
            var before = Tasks(history);
            var after = new Dictionary<string, BoardTask>(before);
            if ((change(after) ?? Problem(before, Promote(after))) is { } problem)
            {
                return (false, problem);
            }

            var changed = after.Values.Where(task => !before.TryGetValue(task.Id, out var old) || !Same(old, task)).ToList();
            if (changed.Count == 0)
            {
                return (true, "Nothing changed.");
            }

            var what = string.Join("; ", changed.Select(task => Describe(before.GetValueOrDefault(task.Id), task)));
            var added = new TaskChange(RunId, history.Count == 0 ? 1 : history[^1].Revision + 1, context.AgentId, time.GetUtcNow(), what, reason, changed);
            if (await store.TryAppendAsync(context.Caller.Tenant, added, ct).ConfigureAwait(false))
            {
                foreach (var task in changed.Where(task => before.GetValueOrDefault(task.Id)?.State != task.State))
                {
                    await events.PublishAsync(context, new TaskStatusChanged(task.Id, task.State), ct).ConfigureAwait(false);
                }

                return (true, $"Changed: {what}.");
            }
        }
    }

    /// <summary>Why the board after a change breaks a rule, or null when it breaks none. Only the tasks changed are checked against the configuration, so removing a check from it does not lock the board (TASK-08).</summary>
    private string? Problem(Dictionary<string, BoardTask> before, Dictionary<string, BoardTask> after)
    {
        foreach (var task in after.Values)
        {
            var unchanged = before.TryGetValue(task.Id, out var old) && Same(old, task);
            var from = before.GetValueOrDefault(task.Id)?.State ?? Proposed; // a new task starts proposed
            if (from != task.State && !Transitions.Contains((from, task.State)))
            {
                return $"task {task.Id} cannot go from {from} to {task.State}.";
            }

            if (task.State == Done && (!task.Verified || (task.RequiresReview && !task.Approved)))
            {
                return $"task {task.Id} cannot be done until its checks pass{(task.RequiresReview ? " and a reviewer approves it" : "")}.";
            }

            if (task.DependsOn.FirstOrDefault(dependency => !after.ContainsKey(dependency)) is { } missing)
            {
                return $"task {task.Id} depends on {missing}, which is not on the board.";
            }

            if (!unchanged && task.Checks.FirstOrDefault(check => !options.Checks.ContainsKey(check)) is { } unknown)
            {
                return $"check {unknown} does not exist. Use one of: {string.Join(", ", options.Checks.Keys.Order(StringComparer.Ordinal))}.";
            }
        }

        return Cycle(after) is { } cycle ? $"the dependencies would form a cycle: {cycle}." : null;
    }

    /// <summary>Why the agent may not make the edit, or null when it may (TASK-08, TEAM-02).</summary>
    private string? Refused(BoardTask task, TaskEdit edit)
    {
        if (owner)
        {
            return null;
        }

        if (edit.Checks is not null || edit.Budget is not null || edit.RequiresReview is not null)
        {
            return "only the owner changes a task's checks, budget or review requirement.";
        }

        if (context.Lead)
        {
            return null;
        }

        if (edit.Assignee is not null || edit.State == Cancelled)
        {
            return "only the lead or the owner assigns or cancels a task.";
        }

        if (task.State == Failed && edit.State is not null)
        {
            return "a failed task goes back to the lead; only the lead or the owner retries it.";
        }

        if (task.Assignee is null && task.State is Proposed or Ready)
        {
            return null;
        }

        if (task.Assignee != context.AgentId || task.State is not (InProgress or Blocked))
        {
            return $"task {task.Id} is {task.State}{(task.Assignee is { } assignee ? $" with {assignee}" : "")}; only the lead or the owner changes it now.";
        }

        return edit with { State = null } == new TaskEdit() ? null : "only the lead or the owner changes a task once it is claimed; you can block or unblock your own.";
    }

    /// <summary>A failed attempt: the task goes back to its author, or, after the last attempt, to the lead (TASK-09, WS-03).</summary>
    private BoardTask Rework(BoardTask task) =>
        task.FailedAttempts + 1 >= options.Capabilities.TaskBoard.MaxAttempts
            ? task with { State = Failed, Assignee = null, FailedAttempts = task.FailedAttempts + 1, Verified = false, Approved = false }
            : task with { State = InProgress, FailedAttempts = task.FailedAttempts + 1, Verified = false, Approved = false };

    /// <summary>Makes a proposed task ready once its dependencies are all done, and a ready one proposed again when they no longer are (TASK-03).</summary>
    private static Dictionary<string, BoardTask> Promote(Dictionary<string, BoardTask> tasks)
    {
        foreach (var task in tasks.Values.Where(task => task.State is Proposed or Ready).ToList())
        {
            tasks[task.Id] = task with { State = task.DependsOn.All(dependency => tasks.GetValueOrDefault(dependency)?.State == Done) ? Ready : Proposed };
        }

        return tasks;
    }

    /// <summary>A cycle of dependencies, such as <c>a → b → a</c>, found by a depth-first search, or null when there is none (TASK-03).</summary>
    private static string? Cycle(Dictionary<string, BoardTask> tasks)
    {
        var visited = new HashSet<string>();
        var path = new List<string>();
        return tasks.Keys.Select(Visit).FirstOrDefault(cycle => cycle is not null);

        string? Visit(string id)
        {
            if (path.Contains(id))
            {
                return string.Join(" → ", path[path.IndexOf(id)..].Append(id));
            }

            if (!visited.Add(id))
            {
                return null;
            }

            path.Add(id);
            var cycle = tasks[id].DependsOn.Select(Visit).FirstOrDefault(found => found is not null);
            path.RemoveAt(path.Count - 1);
            return cycle;
        }
    }

    private static BoardTask Apply(BoardTask task, TaskEdit edit) => task with
    {
        Title = edit.Title ?? task.Title,
        Description = edit.Description ?? task.Description,
        AcceptanceCriteria = edit.AcceptanceCriteria ?? task.AcceptanceCriteria,
        Checks = edit.Checks ?? task.Checks,
        Role = edit.Role ?? task.Role,
        Assignee = edit.Assignee ?? task.Assignee,
        DependsOn = edit.DependsOn ?? task.DependsOn,
        Priority = edit.Priority ?? task.Priority,
        Budget = edit.Budget ?? task.Budget,
        RequiresReview = edit.RequiresReview ?? task.RequiresReview,
    };

    /// <summary>Each task as its latest change left it.</summary>
    private static Dictionary<string, BoardTask> Tasks(IReadOnlyList<TaskChange> history)
    {
        var tasks = new Dictionary<string, BoardTask>(StringComparer.Ordinal);
        foreach (var task in history.SelectMany(change => change.Tasks))
        {
            tasks[task.Id] = task;
        }

        return tasks;
    }

    /// <summary>What changed in a task, field by field.</summary>
    private static string Describe(BoardTask? old, BoardTask task)
    {
        if (old is null)
        {
            return $"{task.Id} added as {task.State}";
        }

        var fields = JsonSerializer.SerializeToElement(old, Json).EnumerateObject().Zip(JsonSerializer.SerializeToElement(task, Json).EnumerateObject())
            .Where(field => !JsonElement.DeepEquals(field.First.Value, field.Second.Value))
            .Select(field => $"{field.First.Name} {Text(field.First.Value)} → {Text(field.Second.Value)}");
        return $"{task.Id} {string.Join(", ", fields)}";

        static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    }

    /// <summary>Tasks compare by value: their lists are new on every read.</summary>
    private static bool Same(BoardTask one, BoardTask other) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(one, Json), JsonSerializer.SerializeToElement(other, Json));

    private static string NoTask(string? id) => $"the board has no task {id}.";
}
