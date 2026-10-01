namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The validation phases that apply so far, numbered as in configuration reference §14.</summary>
public enum ValidationPhase
{
    Parse = 1,
    Shape = 2,
    Merge = 3,
    References = 4,
    Prefix = 9,
    Invariants = 10,
}

/// <summary>One validation error: the setting, what is wrong, and how to fix it (CFG-06).</summary>
/// <param name="Phase">The phase that found it.</param>
/// <param name="Path">The setting, such as <c>agents.developer.model</c>.</param>
/// <param name="Problem">What is wrong, as a sentence.</param>
/// <param name="Fix">How to fix it, as a sentence.</param>
public sealed record ConfigurationError(ValidationPhase Phase, string Path, string Problem, string Fix)
{
    /// <summary>Where the setting was written, such as <c>sof.json:4:7</c>.</summary>
    public string? Location { get; init; }

    public override string ToString()
    {
        var text = $"{(Path.Length == 0 ? "(configuration)" : Path)}: {Problem} {Fix}";
        return Location is null ? text : $"{Location}: {text}";
    }
}

/// <summary>Thrown when something is started with a configuration that does not validate.</summary>
public sealed class ConfigurationException(IReadOnlyList<ConfigurationError> errors)
    : InvalidOperationException("The configuration is not valid:" + string.Concat(errors.Select(error => Environment.NewLine + "  " + error)))
{
    public IReadOnlyList<ConfigurationError> Errors { get; } = errors;
}
