namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>An agent definition. Only <see cref="Instructions"/> is required; everything else has a default (CFG-03).</summary>
public sealed record AgentDefinition
{
    public required string Instructions { get; init; }

    /// <summary>The name of the model profile the agent runs on.</summary>
    public string Model { get; init; } = ModelProfile.DefaultName;
}
