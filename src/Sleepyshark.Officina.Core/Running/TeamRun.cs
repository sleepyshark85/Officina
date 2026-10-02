using System.Text;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tasks;
using Sleepyshark.Officina.Core.Tools;
using static Sleepyshark.Officina.Core.Tasks.TaskState;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>What the team needs of its runner: the board, the stored events, each agent's cancellation, and who is in the team.</summary>
/// <param name="Pipeline">The tool pipeline, whose board the team acts on.</param>
/// <param name="Log">The run's stored events, which say what each agent spent before a restart (INV-07).</param>
/// <param name="CancelOf">The owner's cancellation of an agent, by its id (RUN-06).</param>
/// <param name="Members">Registers the team's agents for the run, so they can message each other; null ends the team and drops its messages.</param>
internal sealed record TeamServices(ToolPipeline Pipeline, IEventLog Log, Func<string, CancellationToken> CancelOf, Action<string, IReadOnlySet<string>?> Members);

/// <summary>
/// The team pattern (TEAM): a lead and agents of several roles over the run's task board. The lead turns the goal into tasks,
/// then each ready task goes to a free agent of its role, which works on it in a turn of its own and submits it; a task that
/// requires a review goes to another agent that can review, and a verified, approved task is done. Work that fails, stalls,
/// runs out of budget or is stopped goes back to the lead with the reason, to retry, reassign, split or leave (TEAM-09). The
/// lead runs the same turns as every other agent (TEAM-02). At most <c>maxParallel</c> agents work at once (TEAM-03), each
/// role's at most its <c>max</c>; every agent is an instance such as <c>developer[2]</c>, with its own turns and budget, and
/// it sees only what it is given: its task, the board for the lead, and messages sent to it (TEAM-04, TEAM-05).
/// </summary>
/// <remarks>
/// The team works from the board as it is, so a run that resumes goes on from its last checkpoint's board: a task in progress
/// goes back to its agent, and the plan is made only while the board is empty (RUN-04).
/// </remarks>
internal sealed class TeamRun
{
    private const string PlanStep = "plan";
    private const string FailedStep = "failed";
    private const string StuckStep = "stuck";
    private const string ReportStep = "report";

    private readonly Steps steps;
    private readonly TeamServices services;
    private readonly OfficinaOptions options;
    private readonly PatternOptions pattern;
    private readonly ToolContext team;
    private readonly string goal;
    private readonly Budget budget;
    private readonly string lead;

    /// <summary>Every agent of the team, by id, with its definition: the lead, then each role's instances.</summary>
    private readonly Dictionary<string, string> members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Budget> budgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentStatus> statuses = new(StringComparer.Ordinal);
    private readonly List<Job> running = [];
    private readonly HashSet<long> told = [];
    private IReadOnlyList<CoreEvent>? stored;
    private long stuckAt = -1;
    private bool reported;

    public TeamRun(Steps steps, TeamServices services, OfficinaOptions options, ToolContext team, PatternOptions pattern, string goal, Budget budget)
    {
        this.steps = steps;
        this.services = services;
        this.options = options;
        this.pattern = pattern;
        this.team = team with { TaskId = null, Instance = null, Lead = false };
        this.goal = goal;
        this.budget = budget;
        lead = pattern.Lead!;
        members[lead] = lead;
        foreach (var (role, settings) in pattern.Roles)
        {
            for (var number = 1; number <= settings.Max; number++)
            {
                members[$"{role}[{number}]"] = role;
            }
        }
    }

    public async Task<StepResult> RunAsync(CancellationToken ct)
    {
        var board = services.Pipeline.Board(team, owner: true)!;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        services.Members(team.RunId, members.Keys.ToHashSet(StringComparer.Ordinal));
        try
        {
            if ((await board.ReadAsync(stop.Token).ConfigureAwait(false)).Count == 0)
            {
                await StartAsync(lead, PlanStep, null, PlanInput(), stop.Token).ConfigureAwait(false);
            }

            while (true)
            {
                var history = await board.HistoryAsync(stop.Token).ConfigureAwait(false);
                var tasks = await board.ReadAsync(stop.Token).ConfigureAwait(false);
                if (await CompleteVerifiedAsync(board, tasks, stop.Token).ConfigureAwait(false))
                {
                    continue; // the board changed, so it is read again
                }

                await DispatchAsync(tasks, history, stop.Token).ConfigureAwait(false);
                await ShowStatusesAsync(tasks, stop.Token).ConfigureAwait(false);
                if (running.Count == 0 && await IdleAsync(tasks, history, stop.Token).ConfigureAwait(false) is { } ended)
                {
                    return ended;
                }

                var finished = await Task.WhenAny(running.Select(job => job.Running)).ConfigureAwait(false);
                var job = running.Single(job => job.Running == finished);
                running.Remove(job);
                if (await EndedAsync(board, job, await finished.ConfigureAwait(false), ct).ConfigureAwait(false) is { } result)
                {
                    return result;
                }
            }
        }
        finally
        {
            // Whatever ended the team, no agent of it goes on working.
            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var job in running)
            {
                try
                {
                    await job.Running.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // It was stopped because the team ended, so its outcome no longer matters; its step's end is published.
                }
            }

            foreach (var member in statuses.Keys.ToList())
            {
                await StatusAsync(member, AgentStatus.Finished, null, CancellationToken.None).ConfigureAwait(false);
            }

            services.Members(team.RunId, null);
        }
    }

    /// <summary>Marks done each task in review that is verified, and approved if it requires a review (TASK-05). Done means integrated, and a team without a workspace has nothing to integrate.</summary>
    private static async Task<bool> CompleteVerifiedAsync(TaskBoard board, IReadOnlyList<BoardTask> tasks, CancellationToken ct)
    {
        var changed = false;
        foreach (var task in tasks.Where(task => task is { State: InReview, Verified: true } && (!task.RequiresReview || task.Approved)))
        {
            changed |= (await board.CompleteAsync(task.Id, "verified and approved", ct).ConfigureAwait(false)).Accepted;
        }

        return changed;
    }

    /// <summary>Starts the work that can start now: the lead on failed tasks, then each task in order of priority.</summary>
    private async Task DispatchAsync(IReadOnlyList<BoardTask> tasks, IReadOnlyList<TaskChange> history, CancellationToken ct)
    {
        var failed = tasks.Where(task => task.State == Failed && !told.Contains(LastChange(history, task.Id).Revision)).ToList();
        if (failed.Count > 0 && Free(lead) && running.Count < pattern.MaxParallel)
        {
            foreach (var task in failed)
            {
                told.Add(LastChange(history, task.Id).Revision);
            }

            await StartAsync(lead, FailedStep, null, FailedInput(failed, history, tasks), ct).ConfigureAwait(false);
        }

        foreach (var task in tasks)
        {
            if (task.State == Ready && Worker(task) is { } worker)
            {
                // The claim is the agent's own, so the board records who works on the task (TASK-04, TASK-07).
                var claimed = await services.Pipeline.Board(Member(worker, task.Id, null))!.ClaimAsync(task.Id, ct).ConfigureAwait(false);
                if (claimed.Accepted)
                {
                    await StartAsync(worker, $"task:{task.Id}", task.Id, WorkInput(worker, task with { State = InProgress, Assignee = worker }, null), ct).ConfigureAwait(false);
                }
            }
            else if (task is { State: InProgress, Assignee: { } assignee } && members.ContainsKey(assignee) && Free(assignee)
                && !running.Any(job => job.Task == task.Id))
            {
                // Back with its agent: changes were asked, or the run resumed while it was being done.
                await StartAsync(assignee, $"task:{task.Id}", task.Id, WorkInput(assignee, task, LastChange(history, task.Id).Reason), ct).ConfigureAwait(false);
            }
            else if (task is { State: InReview, Verified: true, RequiresReview: true, Approved: false } && !running.Any(job => job.Task == task.Id)
                && Reviewer(task) is { } reviewer)
            {
                await StartAsync(reviewer, $"review:{task.Id}", task.Id, ReviewInput(task), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// What ends the team when nobody works: the lead's report once every task is done or cancelled, or the lead's say when the
    /// team cannot go on. A team that still cannot go on after the lead's turn hands off (TEAM-09); null when work started.
    /// </summary>
    private async Task<StepResult?> IdleAsync(IReadOnlyList<BoardTask> tasks, IReadOnlyList<TaskChange> history, CancellationToken ct)
    {
        var revision = history.Count == 0 ? 0 : history[^1].Revision;
        if (tasks.All(task => task.State is Done or Cancelled))
        {
            if (reported)
            {
                return new(StepOutcome.Completed, "");
            }

            reported = true;
            await StartAsync(lead, ReportStep, null, ReportInput(tasks), ct).ConfigureAwait(false);
            return null;
        }

        if (stuckAt == revision)
        {
            var open = string.Join("; ", tasks.Where(task => task.State is not (Done or Cancelled)).Select(task => Line(task)));
            return new(StepOutcome.HandedOff, $"the team cannot go on: {open}", new Handoff(HandoffReason.NoProgress, null, $"the team cannot go on: {open}", goal, [], null, ""));
        }

        stuckAt = revision;
        await StartAsync(lead, StuckStep, null, StuckInput(tasks), ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>What a job's end means for the team: the lead's work decides how the team goes on; an agent's unfinished task goes back to the lead.</summary>
    /// <returns>The team's result when the job ends the team; null when the team goes on.</returns>
    private async Task<StepResult?> EndedAsync(TaskBoard board, Job job, StepResult result, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return steps.Cancelled("the team was cancelled");
        }

        var stopped = result.Outcome == StepOutcome.Cancelled;
        var why = stopped ? $"{job.Member} was stopped" : result.Outcome == StepOutcome.Completed ? null : $"{job.Member}: {result.Output}";
        if (job.Task is null)
        {
            // The lead plans, decides on failures and reports; when it hands off, the team waits for a human (TEAM-09: escalate).
            if (why is not null)
            {
                await StatusAsync(job.Member, AgentStatus.Failed, why, ct).ConfigureAwait(false);
                return result;
            }

            return job.Step == ReportStep || (job.Step == PlanStep && (await board.ReadAsync(ct).ConfigureAwait(false)).Count == 0) ? result : null;
        }

        var task = (await board.ReadAsync(ct).ConfigureAwait(false)).FirstOrDefault(task => task.Id == job.Task);
        var unfinished = job.Step.StartsWith("review:", StringComparison.Ordinal)
            ? task is { State: InReview, RequiresReview: true, Approved: false }
            : task?.State == InProgress && task.Assignee == job.Member;
        if (unfinished)
        {
            why ??= job.Step.StartsWith("review:", StringComparison.Ordinal) ? $"{job.Member} ended the review without a verdict" : $"{job.Member} ended its turn without submitting the task";
            await board.FailAsync(job.Task, why, ct).ConfigureAwait(false);
        }

        if (why is not null)
        {
            await StatusAsync(job.Member, AgentStatus.Failed, why, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// TEAM-08: each agent that has worked is idle, or waiting when its own task waits for a review or its role's tasks wait for
    /// their dependencies; one that failed stays failed until it works again.
    /// </summary>
    private async Task ShowStatusesAsync(IReadOnlyList<BoardTask> tasks, CancellationToken ct)
    {
        foreach (var member in statuses.Keys.Where(member => Free(member) && statuses[member] != AgentStatus.Failed).ToList())
        {
            var reviewed = tasks.FirstOrDefault(task => task is { State: InReview, RequiresReview: true, Approved: false } && task.Assignee == member);
            var waiting = tasks.FirstOrDefault(task => task.State == Proposed && (task.Role ?? members[member]) == members[member] && member != lead);
            if (reviewed is not null || waiting is not null)
            {
                await StatusAsync(member, AgentStatus.Waiting, reviewed is not null ? $"for the review of {reviewed.Id}" : $"for the tasks {waiting!.Id} depends on", ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await StatusAsync(member, AgentStatus.Idle, null, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Starts an agent's turn on its work, within its own budget, cancelled with the team or by the owner.</summary>
    private async Task StartAsync(string member, string step, string? taskId, string input, CancellationToken ct)
    {
        var context = Member(member, taskId, step);
        var memberBudget = await BudgetAsync(member, ct).ConfigureAwait(false);
        var owner = services.CancelOf(member);
        await StatusAsync(member, AgentStatus.Working, step, ct).ConfigureAwait(false);
        // On the thread pool, so a turn that does not yield soon still runs beside the others (TEAM-03).
        running.Add(new(member, step, taskId, Task.Run(RunJobAsync, CancellationToken.None)));

        async Task<StepResult> RunJobAsync()
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, owner);
            return await steps.RunStepAsync(context, new StepOptions { Agent = members[member] }, input, memberBudget, cancel.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An agent's budget over all its turns in the run, from the team's (RUN-05): its definition's <c>budget.total</c>, each
    /// instance its own, with what it spent before a restart.
    /// </summary>
    private async Task<Budget> BudgetAsync(string member, CancellationToken ct)
    {
        if (budgets.TryGetValue(member, out var found))
        {
            return found;
        }

        if (options.Agents[members[member]].Budget.Total is not { } total)
        {
            return budgets[member] = budget;
        }

        stored ??= await services.Log.ReadAsync(team.Caller.Tenant, team.RunId, 0, ct).ConfigureAwait(false);
        return budgets[member] = budget.DrawAgent(total, Spent.OfAgent(stored, member));
    }

    /// <summary>Publishes an agent's status when it changes (TEAM-08).</summary>
    private async Task StatusAsync(string member, AgentStatus status, string? detail, CancellationToken ct)
    {
        if (statuses.TryGetValue(member, out var now) && now == status && status != AgentStatus.Working)
        {
            return;
        }

        statuses[member] = status;
        await steps.PublishAsync(Member(member, null, null), new AgentStatusChanged(status, detail), ct).ConfigureAwait(false);
    }

    /// <summary>An agent of the team, as its turns act: its definition, its instance, the lead's authority for the lead only, and its task.</summary>
    private ToolContext Member(string member, string? taskId, string? step) =>
        team with { Agent = members[member], Instance = member, Lead = member == lead, TaskId = taskId, Step = step };

    private bool Free(string member) => running.All(job => job.Member != member);

    /// <summary>Who does a ready task: the agent the lead assigned it to, or a free agent of its role, or of any role when it names none.</summary>
    private string? Worker(BoardTask task)
    {
        if (running.Count >= pattern.MaxParallel)
        {
            return null;
        }

        if (task.Assignee is { } assignee)
        {
            return assignee != lead && members.ContainsKey(assignee) && Free(assignee) ? assignee : null;
        }

        return members.Keys.FirstOrDefault(member => member != lead && (task.Role is null || members[member] == task.Role) && Free(member));
    }

    /// <summary>A free agent, other than the author, whose tools can review a task: one of a role, or else the lead (TASK-06).</summary>
    private string? Reviewer(BoardTask task)
    {
        if (running.Count >= pattern.MaxParallel)
        {
            return null;
        }

        return members.Keys.Where(member => member != lead).Append(lead)
            .FirstOrDefault(member => member != task.Assignee && Free(member) && Reviews(members[member]));
    }

    /// <summary>Whether the agents of a definition are offered the review tool.</summary>
    private bool Reviews(string definition) =>
        options.Agents[definition].Tools.SelectMany(set => options.ToolSets[set]).Any(tool => options.Tools[tool].BuiltinTool() == "tasks.review");

    private static TaskChange LastChange(IReadOnlyList<TaskChange> history, string id) => history.Last(change => change.Tasks.Any(task => task.Id == id));

    private string Team() => string.Join(", ", members.Keys.Select(member => member == lead ? $"{member} (the lead)" : member));

    private string PlanInput() =>
        $"""
        You lead a team: {Team()}. Turn the goal below into tasks on the board with tasks.create: each a piece of work one agent can do and submit, with its acceptance criteria, the role that does it ({string.Join(", ", pattern.Roles.Keys)}), the tasks it depends on, and whether it needs a review. The team's agents then do them, and you hear back when a task fails.

        Goal:
        {goal}
        """;

    private string FailedInput(IReadOnlyList<BoardTask> failed, IReadOnlyList<TaskChange> history, IReadOnlyList<BoardTask> tasks) =>
        $"""
        Tasks failed and came back to you: {string.Join(", ", failed.Select(task => task.Id))}. For each, retry it (tasks.update with state ready), assign it to another agent of the team, split it into new tasks and cancel it, or leave it failed for the owner.

        Goal:
        {goal}

        {Labels.Data("board", string.Join('\n', failed.Select(task => $"{task.Id} failed: {LastChange(history, task.Id).Reason}")) + "\n\n" + Board(tasks))}
        """;

    private string StuckInput(IReadOnlyList<BoardTask> tasks) =>
        $"""
        No task can start and nobody is working: the open tasks are failed, blocked, wait for tasks that will not be done, or need a role or reviewer the team does not have. Change the board so the work can go on, or leave it as it is to stop the team.

        Goal:
        {goal}

        {Labels.Data("board", Board(tasks))}
        """;

    private string ReportInput(IReadOnlyList<BoardTask> tasks) =>
        $"""
        Every task is done or cancelled. Report what the team achieved towards the goal.

        Goal:
        {goal}

        {Labels.Data("board", Board(tasks))}
        """;

    private string WorkInput(string member, BoardTask task, string? returned) =>
        $"""
        You are {member} in a team: {Team()}. Do task {task.Id}, then submit it with tasks.submit_for_review; its checks run then. {(returned is null ? "" : "It came back to you; the board says why.")}

        {Labels.Data("task", Describe(task) + (returned is null ? "" : $"\nCame back: {returned}"))}
        """;

    private static string ReviewInput(BoardTask task) =>
        $"""
        Review task {task.Id}, which {task.Assignee} did: check its work against its acceptance criteria, then approve it or ask for changes with tasks.review, giving your reasons.

        {Labels.Data("task", Describe(task))}
        """;

    /// <summary>The board, one task after another, for the lead.</summary>
    private static string Board(IReadOnlyList<BoardTask> tasks) => string.Join("\n\n", tasks.Select(Describe));

    /// <summary>A task as an agent reads it: written by agents, so it is given as data (INV-08).</summary>
    private static string Describe(BoardTask task)
    {
        var text = new StringBuilder(Line(task));
        if (task.Description.Length > 0)
        {
            text.Append('\n').Append(task.Description);
        }

        foreach (var (index, criterion) in task.AcceptanceCriteria.Index())
        {
            text.Append(index == 0 ? "\nAcceptance criteria:" : "").Append("\n- ").Append(criterion);
        }

        if (task.Artifacts.Count > 0)
        {
            text.Append("\nSubmitted: ").AppendJoin(", ", task.Artifacts);
        }

        return text.ToString();
    }

    private static string Line(BoardTask task) =>
        $"{task.Id} {task.Title}: {task.State}"
        + (task.Role is { } role ? $", role {role}" : "")
        + (task.Assignee is { } assignee ? $", with {assignee}" : "")
        + (task.DependsOn.Count > 0 ? $", depends on {string.Join(", ", task.DependsOn)}" : "")
        + (task.FailedAttempts > 0 ? $", {task.FailedAttempts} failed attempts" : "")
        + (task.RequiresReview ? ", needs a review" : "");

    /// <summary>An agent's turn on the team's work.</summary>
    /// <param name="Member">The agent, by its id.</param>
    /// <param name="Step">What it does, as the step its events name.</param>
    /// <param name="Task">The task it works on or reviews; null for the lead's work on the whole board.</param>
    /// <param name="Running">The turn.</param>
    private sealed record Job(string Member, string Step, string? Task, Task<StepResult> Running);
}
