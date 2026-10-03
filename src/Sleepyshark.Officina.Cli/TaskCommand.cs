using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The owner's <c>board</c> and <c>task</c> commands (TASK-08): <c>board</c> shows a run's task board, and <c>task</c> adds, edits,
/// reprioritises, reassigns, cancels or shows a task. Each change goes through the board as the owner, an authority the host gives
/// and no name can take, so the board's rules hold (dependencies, cycles, the status changes it allows) and the change is recorded
/// with who, when, what and why (TASK-07). What the board refuses is printed as an error.
/// </summary>
internal sealed class TaskCommand
{
    /// <summary>The subcommands that change the board, which a stored run takes its run's lock for.</summary>
    private static readonly HashSet<string> Changing = ["add", "edit", "priority", "assign", "cancel"];

    private readonly Argument<string[]> title = new("title") { Description = "What the task is.", Arity = ArgumentArity.OneOrMore };
    private readonly Argument<string> id = new("id") { Description = "The task's id, as the board shows it." };
    private readonly Argument<int> priority = new("priority") { Description = "Higher comes first." };
    private readonly Argument<string> agent = new("agent") { Description = "The agent that does it, such as developer[1] in a team." };
    private readonly Argument<string[]> why = new("reason") { Description = "Why it is cancelled.", Arity = ArgumentArity.OneOrMore };
    private readonly Option<string> newTitle = new("--title") { Description = "A new title." };
    private readonly Option<string> description = new("--description") { Description = "What the work is, in more words." };
    private readonly Option<string[]> criteria = new("--criteria") { Description = "An acceptance criterion; repeat it for several. On edit, they replace the task's.", AllowMultipleArgumentsPerToken = false };
    private readonly Option<string> depends = new("--depends") { Description = "The tasks it depends on, such as a,b; \"\" for none." };
    private readonly Option<string> role = new("--role") { Description = "The role that does it." };
    private readonly Option<int?> rank = new("--priority") { Description = "Higher comes first (default 0)." };
    private readonly Option<string> checks = new("--checks") { Description = "The checks that must pass before it is in review, such as build,tests; \"\" for none." };
    private readonly Option<decimal?> budget = new("--budget") { Description = "The most its turns may cost, in USD." };
    private readonly Option<bool?> review = new("--review") { Description = "Whether it requires a review: true or false." };
    private readonly Option<string> reason = new("--reason") { Description = "Why, as the board records it." };

    public TaskCommand()
    {
        var add = new Command("add", "Add a task. It is ready once its dependencies are done.") { title };
        var edit = new Command("edit", "Change a task's fields.") { id, newTitle };
        foreach (var command in new[] { add, edit })
        {
            foreach (var option in new Option[] { description, criteria, depends, role, rank, checks, budget, review, reason })
            {
                command.Options.Add(option);
            }
        }

        Command = new Command("task", "Add, edit, reprioritise, reassign, cancel or show a task of the run's board.")
        {
            add,
            edit,
            new Command("priority", "Reprioritise a task.") { id, priority, reason },
            new Command("assign", "Give a task to an agent.") { id, agent, reason },
            new Command("cancel", "Cancel a task that has not ended.") { id, why },
            new Command("show", "Show a task, with every change to it.") { id },
            new HelpOption(),
        };
    }

    /// <summary>The <c>task</c> command, which the chat's completion walks too.</summary>
    public Command Command { get; }

    /// <summary>Whether the owner's words change a board, rather than only read it.</summary>
    public static bool Changes(IReadOnlyList<string> words) => words is ["task", var sub, ..] && Changing.Contains(sub);

    /// <summary>The <c>task</c> command's help.</summary>
    public static string HelpText() => Help(new TaskCommand().Command.Parse(["--help"]));

    /// <summary>
    /// Carries out a <c>board</c> or <c>task</c> command on <paramref name="target"/>; returns what to print. The board's refusals
    /// are errors, and so are an agent or a role that the run's agent does not have.
    /// </summary>
    /// <param name="words">The command's words, the first <c>board</c> or <c>task</c>.</param>
    /// <param name="target">The board, and what the owner is told about it.</param>
    /// <param name="ct">Cancels the command.</param>
    public static Task<string> CarryOutAsync(IReadOnlyList<string> words, OwnerBoard target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.Options.Capabilities.TaskBoard.Enabled)
        {
            return Task.FromResult("error: the task board is off.");
        }

        return words is ["board"] ? BoardAsync(target.Board(), ct) : new TaskCommand().RunAsync(words.Skip(1).ToList(), target, ct);
    }

    /// <summary>The board as it is now, one task a line, the highest priority first.</summary>
    private static async Task<string> BoardAsync(TaskBoard board, CancellationToken ct)
    {
        var tasks = await board.ReadAsync(ct);
        return tasks.Count == 0 ? "the board is empty." : string.Join('\n', tasks.Select(Line));
    }

    private async Task<string> RunAsync(List<string> words, OwnerBoard target, CancellationToken ct)
    {
        var parse = Command.Parse(words);
        if (parse.Errors.Count > 0)
        {
            return string.Join('\n', parse.Errors.Select(error => $"error: {error.Message}").Append($"Type {target.Prefix}task --help for the syntax."));
        }

        if (parse.Action is HelpAction)
        {
            return Help(parse);
        }

        var board = target.Board();
        var name = parse.CommandResult.Command.Name;
        if (name == "show")
        {
            return await ShowAsync(board, parse.GetValue(id)!, ct);
        }

        var before = (await board.ReadAsync(ct)).ToDictionary(task => task.Id, StringComparer.Ordinal);
        var (taskId, edit, said) = name switch
        {
            "add" => (NextId(before), Edit(parse) with { Title = string.Join(' ', parse.GetValue(title)!) }, "added by the owner"),
            "edit" => (parse.GetValue(id)!, Edit(parse) with { Title = parse.GetValue(newTitle) }, "edited by the owner"),
            "priority" => (parse.GetValue(id)!, new TaskEdit { Priority = parse.GetValue(priority) }, "reprioritised by the owner"),
            "assign" => (parse.GetValue(id)!, new TaskEdit { Assignee = parse.GetValue(agent) }, "reassigned by the owner"),
            _ => (parse.GetValue(id)!, new TaskEdit { State = TaskState.Cancelled }, string.Join(' ', parse.GetValue(why)!)),
        };
        if (Unknown(edit, target) is { } unknown)
        {
            return $"error: {unknown}";
        }

        var because = parse.GetResult(reason) is null ? said : parse.GetValue(reason)!;
        var (accepted, text) = name == "add" ? await board.AddAsync(taskId, edit, because, ct) : await board.EditAsync(taskId, edit, because, ct);
        if (!accepted)
        {
            return $"error: {text}";
        }

        var notes = new List<string> { text };
        if (name == "cancel" && target.Live && before.GetValueOrDefault(taskId) is { State: TaskState.InProgress or TaskState.InReview, Assignee: { } working })
        {
            notes.Add($"note: {working} may work on it until its turn ends; {target.Prefix}cancel {working} stops it now.");
        }

        if (target.Note is { } note)
        {
            notes.Add(note);
        }

        return string.Join('\n', notes);
    }

    /// <summary>The fields that add and edit share; an option left out is left as it is.</summary>
    private TaskEdit Edit(ParseResult parse) => new()
    {
        Description = parse.GetValue(description),
        AcceptanceCriteria = parse.GetResult(criteria) is null ? null : parse.GetValue(criteria),
        DependsOn = List(parse.GetValue(depends)),
        Role = parse.GetValue(role),
        Priority = parse.GetValue(rank),
        Checks = List(parse.GetValue(checks)),
        Budget = parse.GetValue(budget),
        RequiresReview = parse.GetValue(review),
    };

    /// <summary>An agent or a role that the run's agent does not have, which no agent of it would ever take the task for.</summary>
    private static string? Unknown(TaskEdit edit, OwnerBoard target)
    {
        var pattern = target.Options.Agents[target.Agent].Pattern;
        var team = pattern.Type == PatternOptions.Team;
        IReadOnlyCollection<string> roles = team ? [.. pattern.Roles.Keys] : [.. target.Options.Agents.Keys];
        IReadOnlyCollection<string> agents = team
            ? [.. pattern.Roles.SelectMany(entry => Enumerable.Range(1, entry.Value.Max).Select(number => $"{entry.Key}[{number}]"))]
            : roles;
        if (edit.Assignee is { } assignee && !agents.Contains(assignee))
        {
            return $"there is no agent {assignee} to assign it to. Use one of: {string.Join(", ", agents.Order(StringComparer.Ordinal))}.";
        }

        return edit.Role is { } named && !roles.Contains(named)
            ? $"there is no role {named}. Use one of: {string.Join(", ", roles.Order(StringComparer.Ordinal))}."
            : null;
    }

    /// <summary>A task with its fields, then every change to it: who, when, what and why (TASK-07).</summary>
    private static async Task<string> ShowAsync(TaskBoard board, string taskId, CancellationToken ct)
    {
        var history = await board.HistoryAsync(ct);
        if (history.SelectMany(change => change.Tasks).LastOrDefault(task => task.Id == taskId) is not { } task)
        {
            return $"error: the board has no task {taskId}.";
        }

        var lines = new List<string> { Line(task) };
        lines.AddRange(new[]
        {
            task.Description.Length > 0 ? $"  description: {task.Description}" : null,
            task.AcceptanceCriteria.Count > 0 ? $"  acceptance criteria: {string.Join("; ", task.AcceptanceCriteria)}" : null,
            task.Checks.Count > 0 ? $"  checks: {string.Join(", ", task.Checks)}" : null,
            task.RequiresReview ? $"  requires a review{(task.Approved ? ", approved" : "")}" : null,
            task.FailedAttempts > 0 ? $"  failed attempts: {task.FailedAttempts}" : null,
            task.Artifacts.Count > 0 ? $"  artifacts: {string.Join(", ", task.Artifacts)}" : null,
            "  history:",
        }.OfType<string>());
        lines.AddRange(history.Where(change => change.Tasks.Any(changed => changed.Id == taskId))
            .Select(change => string.Create(CultureInfo.InvariantCulture, $"    {change.Time:u} {change.By}: {change.What} ({change.Reason})")));
        return string.Join('\n', lines);
    }

    /// <summary>A task on one line: its id, title, status, assignee, role, dependencies, priority, and spend of its budget.</summary>
    private static string Line(BoardTask task) =>
        $"{task.Id} {task.Title}: {task.State}{(task.Assignee is { } assignee ? $", with {assignee}" : "")}{(task.Role is { } role ? $", role {role}" : "")}"
        + string.Create(CultureInfo.InvariantCulture, $"{(task.DependsOn.Count > 0 ? $", depends on {string.Join(", ", task.DependsOn)}" : "")}, priority {task.Priority}, ${task.Spent:0.00} of ${task.Budget:0.00}");

    /// <summary>The first id <c>owner-1</c>, <c>owner-2</c>, … that the board does not have.</summary>
    private static string NextId(Dictionary<string, BoardTask> tasks) =>
        Enumerable.Range(1, tasks.Count + 1).Select(number => $"owner-{number}").First(candidate => !tasks.ContainsKey(candidate));

    /// <summary>A comma-separated list; null when the option was left out.</summary>
    private static string[]? List(string? text) =>
        text?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Help(ParseResult parse)
    {
        using var text = new StringWriter();
        parse.Invoke(new InvocationConfiguration { Output = text, Error = text });
        return text.ToString().TrimEnd();
    }
}

/// <summary>Where the owner's board commands act.</summary>
/// <param name="Board">Opens the run's board as the owner.</param>
/// <param name="Options">The run's configuration.</param>
/// <param name="Agent">The run's agent, whose team's agents and roles tasks are given to.</param>
/// <param name="Live">Whether the run is going on, so its team sees a change at its next look.</param>
/// <param name="Prefix">What the owner types before a command: a slash in a chat session.</param>
/// <param name="Note">What the owner is told after a change, if anything.</param>
internal sealed record OwnerBoard(Func<TaskBoard> Board, OfficinaOptions Options, string Agent, bool Live, string Prefix, string? Note = null);
