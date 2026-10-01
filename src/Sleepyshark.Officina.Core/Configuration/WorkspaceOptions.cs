using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The git workspace (WS).</summary>
public sealed record WorkspaceOptions
{
    [Setting("Paths agents cannot see or change, in addition to the fixed ones: `.git`, `**/.env*` and `.sof/**` are hidden, and `sof.json` and `sof.*.json` are read-only.",
        Example = """[{ "path": "secrets/**", "access": "hidden" }]""")]
    public IReadOnlyList<ProtectedPath> ProtectedPaths { get; init; } = [];

    [Setting("Whether an agent's working copy is kept when its task ends, so the owner can look at it.", Example = "true")]
    public bool KeepWorkingCopies { get; init; }
}

/// <summary>A path agents cannot see or change (WS-05).</summary>
public sealed record ProtectedPath
{
    [Setting("A glob relative to the workspace root, where `**` matches any number of folders.", Example = "\"secrets/**\"")]
    [Required(ErrorMessage = Messages.Required)]
    public required string Path { get; init; }

    [Setting("`hidden`: agents cannot see it at all; `readOnly`: they can read it but not change it.", Example = "\"readOnly\"")]
    public PathAccess Access { get; init; } = PathAccess.Hidden;
}

public enum PathAccess
{
    Hidden,
    ReadOnly,
}
