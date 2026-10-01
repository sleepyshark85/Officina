using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>A gate: a deterministic rule that runs before each call of the tools it is attached to (TOOL-05).</summary>
public sealed record GateOptions
{
    /// <summary>The built-in gate that asks a human when its condition holds.</summary>
    public const string RequireApproval = "builtin:require-approval";

    /// <summary>The built-in gate that denies the call when its condition holds.</summary>
    public const string Deny = "builtin:deny";

    [Setting("What the gate runs: `builtin:require-approval` or `builtin:deny`, which act when `when` holds, or `extension:<id>` for a gate the application registers.",
        Example = "\"builtin:require-approval\"")]
    [Required(ErrorMessage = Messages.Required)]
    public required string Use { get; init; }

    [Setting("For the built-in gates: the condition over the tool's arguments under which the gate acts. Unset means always.",
        Example = """{ "field": "args.branch", "in": ["main", "master"] }""")]
    public Condition? When { get; init; }

    /// <summary>The id of the application's gate, for an <c>extension:</c> gate.</summary>
    public string? ExtensionId() => ToolOptions.After(Use, "extension:");
}
