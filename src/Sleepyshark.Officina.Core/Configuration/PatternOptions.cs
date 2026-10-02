using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// How an agent does its work (PAT-01): in a turn of its own, or in a pattern of steps, each a turn of an agent or a
/// nested pattern (PAT-02). The configuration binder cannot choose a type by a field's value, so the settings of every
/// pattern are here, and only those of <see cref="Type"/> apply.
/// </summary>
public sealed record PatternOptions
{
    public const string ToolLoop = "toolLoop";
    public const string SingleCall = "singleCall";
    public const string Workflow = "workflow";
    public const string Router = "router";
    public const string FanOut = "fanOut";
    public const string EvaluateAndRevise = "evaluateAndRevise";
    public const string PlanAndExecute = "planAndExecute";
    public const string Team = "team";

    internal static readonly string[] BuiltIn = [ToolLoop, SingleCall, Workflow, Router, FanOut, EvaluateAndRevise, PlanAndExecute, Team];

    [Setting("`toolLoop`: a turn that calls tools until a stop condition holds; `singleCall`: a turn with no tools; `workflow`, `router`, `fanOut`, `evaluateAndRevise`, `planAndExecute` or `team`; or `extension:<id>` for a pattern the application registers, which reads the settings here that it needs.",
        Example = "\"router\"")]
    [Required(ErrorMessage = Messages.Required)]
    public string Type { get; init; } = ToolLoop;

    [Setting("workflow: the steps, run in order from the first. Each needs an `id`.", Example = """[{ "id": "draft" }, { "id": "review", "agent": "reviewer", "input": ["draft"] }]""")]
    public IReadOnlyList<StepOptions> Steps { get; init; } = [];

    [Setting("workflow: where to go after a step completes. The first rule from that step whose `when` holds decides; with none, the next step follows.",
        Example = """[{ "from": "triage", "when": { "field": "output.kind", "is": "question" }, "goto": "end" }]""")]
    public IReadOnlyList<BranchRule> Next { get; init; } = [];

    [Setting("router: the step that classifies the input. Its output must be structured. Unset: a turn of the agent itself.", Example = """{ "agent": "classifier" }""")]
    public StepOptions? Classify { get; init; }

    [Setting("router: the field of the classification that picks the route. fanOut with `combine: majority`: the field the branches vote on.", Example = "\"output.route\"")]
    public string? On { get; init; }

    [Setting("router: the step for each value of `on`, by value. It gets the router's input.", Example = """{ "bug": { "agent": "developer" }, "docs": { "agent": "writer" } }""")]
    public IReadOnlyDictionary<string, StepOptions> Routes { get; init; } = new Dictionary<string, StepOptions>();

    [Setting("router: the route taken when no route has the value. Unset: the work is handed off, as no route for a value.", Example = "\"bug\"")]
    public string? Otherwise { get; init; }

    [Setting("fanOut: the steps that run in parallel on the input; with `over`, the one step that runs on each item.", Example = """[{ "agent": "reviewer" }, { "agent": "tester" }]""")]
    public IReadOnlyList<StepOptions> Branches { get; init; } = [];

    [Setting("fanOut: a list in the input, which must then be JSON, such as `input.files`. The branch runs once for each item.", Example = "\"input.files\"")]
    public string? Over { get; init; }

    [Setting("fanOut: the most branches that run at once. team: the most agents, the lead included, that work at once.", Example = "2")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int MaxParallel { get; init; } = 4;

    [Setting("fanOut: how the branches' results are combined. `all`: every branch must complete, and the output is the JSON list of their outputs; `firstSuccess`: the first to complete, and the others are stopped; `majority`: the output of a branch whose value of `on` more than half of the branches share; `step`: `combiner` combines their outputs.",
        Example = "\"majority\"")]
    public FanOutCombine Combine { get; init; }

    [Setting("fanOut with `combine: step`: the step that gets the branches' outputs and combines them.", Example = """{ "agent": "editor" }""")]
    public StepOptions? Combiner { get; init; }

    [Setting("evaluateAndRevise: the step that produces the work. Unset: a turn of the agent itself.", Example = """{ "agent": "developer" }""")]
    public StepOptions? Generate { get; init; }

    [Setting("evaluateAndRevise: the checks, by name in `checks`, that the work must pass, in order. The first failure's findings go back to `generate`, masked, with the input and the work.",
        Example = """["build", "tests"]""")]
    public IReadOnlyList<string> Checks { get; init; } = [];

    [Setting("evaluateAndRevise: how many times the work is revised before it is handed off, as an output check failure.", Example = "5")]
    [Range(0, int.MaxValue, ErrorMessage = "must be zero or more.")]
    public int MaxRevisions { get; init; } = 3;

    [Setting("planAndExecute: the step that writes the plan: structured output with a `steps` list. Unset: a turn of the agent itself.", Example = """{ "agent": "planner" }""")]
    public StepOptions? Planner { get; init; }

    [Setting("planAndExecute: the step that carries out each item of the plan's `steps`, in order.", Example = """{ "agent": "developer" }""")]
    public StepOptions? Executor { get; init; }

    [Setting("planAndExecute: how many times the planner is asked for a new plan after a step does not complete, before the work is handed off.", Example = "1")]
    [Range(0, int.MaxValue, ErrorMessage = "must be zero or more.")]
    public int MaxReplans { get; init; } = 2;

    [Setting("team: the agent, by name in `agents`, that plans the work as tasks on the board, decides on the tasks that fail, and reports. It works in turns of its own.", Example = "\"lead\"")]
    public string? Lead { get; init; }

    [Setting("team: the agents, by name in `agents`, that do and review the tasks, with how many of each work at once. Each is an agent of its own, such as `developer[2]`, which works in turns of its own and keeps no history.", Example = """{ "developer": { "max": 3 }, "reviewer": { "max": 1 } }""")]
    public IReadOnlyDictionary<string, RoleOptions> Roles { get; init; } = new Dictionary<string, RoleOptions>();

    /// <summary>Whether the pattern is a turn of the agent itself rather than steps.</summary>
    internal bool IsTurn => Type is ToolLoop or SingleCall;

    /// <summary>The id of the application's pattern, for an <c>extension:</c> pattern.</summary>
    public string? ExtensionId() => ToolOptions.After(Type, "extension:");

    /// <summary>Every step of the pattern, with its setting path, without the steps of nested patterns.</summary>
    internal IEnumerable<(string Path, StepOptions Step)> Children(string path) =>
        Steps.Select((step, index) => ($"{path}.steps[{index}]", step))
            .Concat(Routes.Select(route => ($"{path}.routes.{route.Key}", route.Value)))
            .Concat(Branches.Select((step, index) => ($"{path}.branches[{index}]", step)))
            .Concat(new[] { ("classify", Classify), ("combiner", Combiner), ("generate", Generate), ("planner", Planner), ("executor", Executor) }
                .Where(step => step.Item2 is not null).Select(step => ($"{path}.{step.Item1}", step.Item2!)));

    /// <summary>This pattern and every pattern nested in it, with their setting paths.</summary>
    public IEnumerable<(string Path, PatternOptions Pattern)> Nested(string path) =>
        Children(path).Where(child => child.Step.Pattern is not null)
            .SelectMany(child => child.Step.Pattern!.Nested($"{child.Path}.pattern")).Prepend((path, this));

    /// <summary>The agents that steps of the pattern name, nested patterns included.</summary>
    internal IEnumerable<string> Agents() => Nested("").SelectMany(nested => nested.Pattern.Children("")).Select(child => child.Step.Agent).OfType<string>();
}

/// <summary>
/// A step of a pattern: a turn of an agent, the agent's own pattern, or a nested pattern (PAT-02). It gets only the
/// input the pattern passes it (PAT-04).
/// </summary>
public sealed record StepOptions
{
    [Setting("workflow: the step's name, which `input`, `next` and `goto:` use. It cannot be `input` or `end`.", Example = "\"triage\"")]
    public string? Id { get; init; }

    [Setting("The agent, by name in `agents`, whose work the step is: its turn, or its own pattern. Unset, with no `pattern`: a turn of the agent the pattern belongs to.",
        Example = "\"reviewer\"")]
    public string? Agent { get; init; }

    [Setting("A nested pattern, of the agent the pattern belongs to.", Example = """{ "type": "evaluateAndRevise", "checks": ["tests"] }""")]
    public PatternOptions? Pattern { get; init; }

    [Setting("workflow: what the step gets: `input` for the workflow's input, or an earlier step's id for its output. Several are each labelled with their source. Unset: the workflow's input.",
        Example = """["input", "triage"]""")]
    public IReadOnlyList<string>? Input { get; init; }

    [Setting("workflow: what happens after each outcome of the step.", Example = """{ "failed": "retry:1", "handedOff": "goto:escalate" }""")]
    [Required(ErrorMessage = Messages.Required)]
    public OutcomeActions OnOutcome { get; init; } = new();
}

/// <summary>What a workflow does after each outcome of a step (PAT-08). A cancelled step always ends the run.</summary>
public sealed record OutcomeActions
{
    [Setting("After the step completes: `continue` to the step `next` picks, `retry:<n>`, `goto:<step>` or `handoff` to end the workflow with the step's result.", Example = "\"goto:publish\"")]
    public string Completed { get; init; } = "continue";

    [Setting("After the step is handed off: `continue`, `retry:<n>`, `goto:<step>` or `handoff`.", Example = "\"retry:1\"")]
    public string HandedOff { get; init; } = "handoff";

    [Setting("After the step fails: `continue`, `retry:<n>`, `goto:<step>` or `handoff`.", Example = "\"retry:2\"")]
    public string Failed { get; init; } = "handoff";

    /// <summary>An action, such as <c>retry:2</c>, as its kind and argument; a null kind for text that is not an action.</summary>
    internal static (string? Kind, string? Argument) Parse(string action) => action.Split(':', 2) switch
    {
        ["continue" or "handoff"] => (action, null),
        ["retry", var times] when int.TryParse(times, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0 => ("retry", times),
        ["goto", { Length: > 0 } step] => ("goto", step),
        _ => (null, null),
    };
}

/// <summary>A conditional branch of a workflow (PAT-03). Its condition reads the step's structured output only.</summary>
public sealed record BranchRule
{
    /// <summary>Where a workflow ends.</summary>
    public const string End = "end";

    [Setting("The step, by id, after which the rule applies.", Example = "\"triage\"")]
    [Required(ErrorMessage = Messages.Required)]
    public string? From { get; init; }

    [Setting("The condition on the step's structured output, such as `output.kind`. Unset means always.", Example = """{ "field": "output.kind", "is": "question" }""")]
    public Condition? When { get; init; }

    [Setting("The step, by id, to go to, or `end`.", Example = "\"end\"")]
    [Required(ErrorMessage = Messages.Required)]
    public string? Goto { get; init; }
}

/// <summary>An agent that can join a team (TEAM-01).</summary>
public sealed record RoleOptions
{
    [Setting("How many of the agent can work at once.", Example = "3")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int Max { get; init; } = 1;
}

public enum FanOutCombine
{
    All,
    FirstSuccess,
    Majority,
    Step,
}
