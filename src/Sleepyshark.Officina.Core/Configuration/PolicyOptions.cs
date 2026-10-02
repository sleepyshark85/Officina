using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Policies for every tool call: permission rules, gates for all tools, and what anonymous callers may do. And for
/// work from outside the core, at admission: masking and rate limits (ING-01).
/// </summary>
public sealed record PolicyOptions
{
    [Setting("Permission rules, in order. The first that matches a call decides it; a call no rule matches goes on.",
        Example = """[{ "tool": "delete_file", "action": "ask" }]""")]
    public IReadOnlyList<PermissionRule> PermissionRules { get; init; } = [];

    [Setting("Gates, by name in `gates`, that run before every tool call, ahead of the tool's own gates.", Example = """["no-main-branch"]""")]
    public IReadOnlyList<string> Gates { get; init; } = [];

    [Setting("The permissions a caller without an identity holds.", Example = """["issues:read"]""")]
    public IReadOnlyList<string> AnonymousPermissions { get; init; } = [];

    [Setting("Masking of personal data in work from outside, before the model, history or logs see it. On by default.", Example = """{ "enabled": false }""")]
    [Required(ErrorMessage = Messages.Required)]
    public MaskingOptions Masking { get; init; } = new();

    [Setting("How much work each owner, each tenant and each run may take in.", Example = """{ "perOwner": { "permits": 20, "window": "01:00:00" } }""")]
    [Required(ErrorMessage = Messages.Required)]
    public RateLimitOptions RateLimits { get; init; } = new();
}
