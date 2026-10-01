namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Policies for every tool call: permission rules, gates for all tools, and what anonymous callers may do.</summary>
public sealed record PolicyOptions
{
    [Setting("Permission rules, in order. The first that matches a call decides it; a call no rule matches goes on.",
        Example = """[{ "tool": "delete_file", "action": "ask" }]""")]
    public IReadOnlyList<PermissionRule> PermissionRules { get; init; } = [];

    [Setting("Gates, by name in `gates`, that run before every tool call, ahead of the tool's own gates.", Example = """["no-main-branch"]""")]
    public IReadOnlyList<string> Gates { get; init; } = [];

    [Setting("The permissions a caller without an identity holds.", Example = """["issues:read"]""")]
    public IReadOnlyList<string> AnonymousPermissions { get; init; } = [];
}
