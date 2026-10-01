namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>One validation error: the setting, what is wrong, and how to fix it (CFG-06).</summary>
/// <param name="Phase">The phase that found it.</param>
/// <param name="Path">The setting, such as <c>agents.developer.model</c>.</param>
/// <param name="Problem">What is wrong, as a sentence.</param>
/// <param name="Fix">How to fix it, as a sentence; empty when <paramref name="Problem"/> already says.</param>
public sealed record ConfigurationError(ValidationPhase Phase, string Path, string Problem, string Fix)
{
    /// <summary>Where the setting was written, such as <c>sof.json:4:7</c>.</summary>
    public string? Location { get; init; }

    public override string ToString()
    {
        var text = $"{(Path.Length == 0 ? "(configuration)" : Path)}: {Problem} {Fix}".TrimEnd();
        return Location is null ? text : $"{Location}: {text}";
    }
}
