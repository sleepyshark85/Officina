namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Optional capabilities, each off until it is set (CAP-01). S07 adds the others and the checks between them.</summary>
public sealed record CapabilitiesOptions
{
    [Setting("The git workspace: a working copy per agent, and an integration queue into the baseline. Off when unset.",
        Example = """{ "protectedPaths": [{ "path": "secrets/**", "access": "hidden" }] }""")]
    public WorkspaceOptions? Workspace { get; init; }

    [Setting("The sandbox that commands run in: no network unless allowed, and command rules. Off when unset.",
        Example = """{ "allowedHosts": ["api.nuget.org"] }""")]
    public SandboxOptions? Sandbox { get; init; }
}
