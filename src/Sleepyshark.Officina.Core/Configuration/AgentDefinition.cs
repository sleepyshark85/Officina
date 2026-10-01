namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// An agent definition. Only <see cref="Instructions"/> is required (CFG-03, CFG-17). Files build one definition
/// on another with <c>extends</c>; code uses a <c>with</c> expression (CFG-05).
/// </summary>
public sealed record AgentDefinition
{
    [Setting("What the agent is for. Usable in instructions as `{{agent.description}}`.", Example = "\"Implements one task.\"")]
    public string? Description { get; init; }

    [Setting("The agent's job. Required: only the application knows it (CFG-17). Placeholders may use the `project` and `agent` namespaces only (CFG-14).",
        Example = "\"Extract the invoice number, date and total. Reply as JSON.\"")]
    public required string Instructions { get; init; }

    [Setting("The name of the model profile the agent runs on (MDL-03).", Example = "\"strong\"")]
    public string Model { get; init; } = ModelProfile.DefaultName;
}
