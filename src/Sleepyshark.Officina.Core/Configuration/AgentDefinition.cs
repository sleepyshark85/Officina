using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// An agent definition. Only <see cref="Instructions"/> is required (CFG-03, CFG-17). Files build one definition on
/// another with <c>extends</c>; code uses a <c>with</c> expression (CFG-05).
/// </summary>
public sealed record AgentDefinition
{
    [Setting("What the agent is for. Usable in instructions as `{{agent.description}}`.", Example = "\"Implements one task.\"")]
    public string? Description { get; init; }

    [Setting("The agent's job. Only the application knows it, so it has no default. Placeholders may use the project and the agent only.",
        Example = "\"Extract the invoice number, date and total. Reply as JSON.\"")]
    [Required(ErrorMessage = Messages.Required)]
    public required string Instructions { get; init; }

    [Setting("The name of the model profile the agent runs on.", Example = "\"strong\"")]
    [Required(ErrorMessage = Messages.Required)]
    public string Model { get; init; } = ModelProfile.DefaultName;

    [Setting("The tool sets, by name in `toolSets`, whose tools the agent is offered. The same tools are offered whoever the caller is.",
        Example = """["files", "issues"]""")]
    public IReadOnlyList<string> Tools { get; init; } = [];

    [Setting("Whether the model is offered only the tools' names, and reads a tool's description and arguments with `describe_tool` when it needs them. For agents with many tools.",
        Example = "true")]
    public bool ToolDescriptionsOnDemand { get; init; }

    [Setting("Narrows the caller's permissions for this agent's tool calls: a permission counts only if the caller holds it and it is listed here. Unset keeps the caller's.",
        Example = """["issues:write"]""")]
    public IReadOnlyList<string>? Permissions { get; init; }

    [Setting("The capabilities, by name in `capabilities`, this agent uses; each must be enabled. Unset uses every enabled capability.",
        Example = """["conversationStore", "knowledge"]""")]
    public IReadOnlyList<string>? Capabilities { get; init; }

    [Setting("The most tool calls from one reply that run at the same time, when every tool called is safe to run in parallel.", Example = "4")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int MaxParallelToolCalls { get; init; } = 4;

    [Setting("How the agent's model input is built.", Example = """{ "operatingFacts": ["Today is {{now:date}}."] }""")]
    [Required(ErrorMessage = Messages.Required)]
    public ContextOptions Context { get; init; } = new();

    [Setting("When a turn is complete. They combine: the first that holds completes the turn.",
        Example = """{ "finished": false, "finishTool": "submit_report" }""")]
    [Required(ErrorMessage = Messages.Required)]
    public StopConditions StopWhen { get; init; } = new();

    // INV-07: a turn always has a budget.
    [Setting("The agent's budgets. They can be high, but they cannot be removed or unlimited.",
        Example = """{ "turn": { "iterations": 50, "cost": 5 } }""")]
    [Required(ErrorMessage = "cannot be removed. Set a limit; it can be high, but it always exists.")]
    public AgentBudget Budget { get; init; } = new();

    [Setting("When a turn has stalled.", Example = """{ "iterationsWithoutProgress": 5 }""")]
    [Required(ErrorMessage = Messages.Required)]
    public StallOptions Stall { get; init; } = new();

    [Setting("Whether a turn ends in a handoff for a policy gap when every tool call of an iteration is refused. With `false`, the refusals go back to the model.",
        Example = "false")]
    public bool HandOffOnPolicyGap { get; init; } = true;
}
