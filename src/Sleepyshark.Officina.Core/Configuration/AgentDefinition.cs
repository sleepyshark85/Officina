namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// An agent definition. Only <see cref="Instructions"/> is required; everything else has a default (CFG-03, CFG-17).
/// In code, a definition builds on another with a <c>with</c> expression; files use <c>extends</c> (CFG-05).
/// </summary>
[FileOnlySetting("extends", FileOnlySettingKind.Text,
    "Another agent definition this one builds on (CFG-05). Its settings are the lower layer; cycles are rejected.",
    Example = "\"base-coder\"")]
public sealed record AgentDefinition
{
    [Setting("What the agent is for. Usable in instructions as `{{agent.description}}`.", Example = "\"Implements one task in its own working copy.\"")]
    public string? Description { get; init; }

    [Setting("The agent's job, in its own words. Required: only the application knows it (CFG-17). Placeholders may use the `project` and `agent` namespaces only (CFG-14).",
        Example = "\"Extract the invoice number, date and total. Reply as JSON.\"", AllowFile = true)]
    public required string Instructions { get; init; }

    [Setting("The model profile of the agent's default model slot: a profile name, or a profile written inline (MDL-03).", Example = "\"strong\"")]
    public ModelReference Model { get; init; } = ModelProfile.DefaultName;

    [Setting("Which of the application's enabled capabilities the agent uses. Unset means all of them (CAP-01, CAP-03).", Example = "[\"workspace\", \"sandbox\"]")]
    public ValueList<string>? Capabilities { get; init; }
}
