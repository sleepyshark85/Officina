namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Which provider and model a model slot uses. S02 adds the remaining settings (MDL-02).</summary>
public sealed record ModelProfile
{
    /// <summary>The name of the profile an agent uses when it names none (CFG-03).</summary>
    public const string DefaultName = "default";

    public string Provider { get; init; } = "claude";

    public string Model { get; init; } = "claude-opus-5-5";
}
