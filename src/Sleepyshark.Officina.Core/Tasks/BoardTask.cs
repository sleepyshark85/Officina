namespace Sleepyshark.Officina.Core.Tasks;

/// <summary>A task's status (TASK-02). <see cref="TaskBoard"/> holds the status changes allowed.</summary>
public enum TaskState
{
    /// <summary>Waiting for its dependencies.</summary>
    Proposed,

    /// <summary>Its dependencies are done, so an agent may claim it.</summary>
    Ready,

    /// <summary>One agent works on it (TASK-04).</summary>
    InProgress,

    /// <summary>Its verification checks passed. It waits for its review, if it requires one, and its integration.</summary>
    InReview,

    /// <summary>Waiting for something outside the board.</summary>
    Blocked,

    /// <summary>Verified, reviewed if it requires a review, and integrated.</summary>
    Done,

    /// <summary>Out of attempts or budget, so back with the lead to retry, split or cancel (TASK-09).</summary>
    Failed,

    /// <summary>Cancelled by the owner.</summary>
    Cancelled,
}

/// <summary>
/// A task on a run's task board, as it is after its latest change (TASK-01). Its history of attempts and changes is in
/// the board's changes (TASK-07).
/// </summary>
public sealed record BoardTask
{
    /// <summary>Unique on the board, chosen by its creator.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = "";

    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];

    /// <summary>The checks, by name in <c>checks</c>, that must all pass before the task is in review (TASK-05).</summary>
    public IReadOnlyList<string> Checks { get; init; } = [];

    /// <summary>The role, an agent definition, that the work needs; null for any.</summary>
    public string? Role { get; init; }

    /// <summary>The task's owner: the agent that works on it, and the author of what it submits (TASK-06).</summary>
    public string? Assignee { get; init; }

    public TaskState State { get; init; }

    /// <summary>The tasks, by id, that must be done before this one is ready (TASK-03).</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Higher comes first.</summary>
    public int Priority { get; init; }

    /// <summary>The most its turns may cost, in USD (TASK-09).</summary>
    public decimal Budget { get; init; }

    /// <summary>What its turns have cost, in USD.</summary>
    public decimal Spent { get; init; }

    public bool RequiresReview { get; init; }

    /// <summary>Whether its verification checks passed on the work now in review.</summary>
    public bool Verified { get; init; }

    /// <summary>Whether a reviewer other than the author approved the work now in review (TASK-06).</summary>
    public bool Approved { get; init; }

    /// <summary>Its attempts that failed a check, a review or an integration since it was last retried (TASK-09).</summary>
    public int FailedAttempts { get; init; }

    /// <summary>What the work produced, such as artifact ids or files, as its author submitted it.</summary>
    public IReadOnlyList<string> Artifacts { get; init; } = [];
}

/// <summary>
/// Changes to a task's fields; a null field is left as it is. The owner may change every field. Agents may not change
/// a task's checks, budget, assignee or review requirement once it exists, nor cancel it.
/// </summary>
public sealed record TaskEdit
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }

    public IReadOnlyList<string>? Checks { get; init; }

    public string? Role { get; init; }

    public string? Assignee { get; init; }

    public IReadOnlyList<string>? DependsOn { get; init; }

    public int? Priority { get; init; }

    public decimal? Budget { get; init; }

    public bool? RequiresReview { get; init; }

    /// <summary><see cref="TaskState.Blocked"/>, <see cref="TaskState.Ready"/> to unblock or retry, or <see cref="TaskState.Cancelled"/>.</summary>
    public TaskState? State { get; init; }
}

/// <summary>
/// One accepted change of a task board, which may change several tasks at once, such as a task done and the tasks that
/// then become ready (TASK-07). Changes are only ever added.
/// </summary>
/// <param name="RunId">The run.</param>
/// <param name="Revision">The board's revision after this change: 1 for the first, and one more for each after it.</param>
/// <param name="By">The agent that made it, or <see cref="TaskBoard.Owner"/>.</param>
/// <param name="Time">When.</param>
/// <param name="What">What changed, field by field.</param>
/// <param name="Reason">Why.</param>
/// <param name="Tasks">The tasks changed, as they are after it.</param>
public sealed record TaskChange(string RunId, long Revision, string By, DateTimeOffset Time, string What, string Reason, IReadOnlyList<BoardTask> Tasks);
