using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>A permission rule. The first rule that matches a call decides it (TOOL-05).</summary>
public sealed record PermissionRule
{
    [Setting("The tool the rule applies to, by name in `tools`.", Example = "\"run_command\"")]
    [Required(ErrorMessage = Messages.Required)]
    public required string Tool { get; init; }

    [Setting("The condition over the tool's arguments under which the rule matches, written as in `gates.<name>.when`. Unset matches every call.",
        Example = """{ "field": "args.command", "in": ["git push"] }""")]
    public Condition? When { get; init; }

    [Setting("What a match decides: `allow`, `deny`, `ask` a human, or `route` to `to`.", Example = "\"deny\"")]
    public PolicyAction Action { get; init; } = PolicyAction.Deny;

    [Setting("Why, as told to the model and the human.", Example = "\"Only the owner pushes.\"")]
    public string? Reason { get; init; }

    [Setting("For `route`: the agent, by name, that the turn is handed to.", Example = "\"lead\"")]
    public string? To { get; init; }
}
