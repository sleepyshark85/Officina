using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>A gate: a deterministic rule that runs before each call of the tools it is attached to (TOOL-05).</summary>
public sealed record GateOptions
{
    /// <summary>The built-in gate that asks a human when its condition holds.</summary>
    public const string RequireApproval = "builtin:require-approval";

    /// <summary>The built-in gate that denies the call when its condition holds.</summary>
    public const string Deny = "builtin:deny";

    /// <summary>The built-in gate that asks a human when its condition holds and the agent has read untrusted content (SEC-04).</summary>
    public const string UntrustedContentApproval = "builtin:untrusted-content-approval";

    [Setting("What the gate runs: `builtin:require-approval` or `builtin:deny`, which act when `when` holds; `builtin:untrusted-content-approval`, which asks when `when` holds and the agent has read untrusted content; or `extension:<id>` for a gate the application registers (`sof` registers `extension:sandbox.commandRules`, the sandbox's command rules, when the sandbox is on).",
        Example = "\"builtin:require-approval\"")]
    [Required(ErrorMessage = Messages.Required)]
    public required string Use { get; init; }

    [Setting("For the built-in gates: the condition over the tool's arguments under which the gate acts. Unset means always.",
        Example = """{ "field": "args.branch", "in": ["main", "master"] }""")]
    public Condition? When { get; init; }

    /// <summary>The id of the application's gate, for an <c>extension:</c> gate.</summary>
    public string? ExtensionId() => ToolOptions.After(Use, "extension:");
}
