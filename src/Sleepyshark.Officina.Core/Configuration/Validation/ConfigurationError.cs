namespace Sleepyshark.Officina.Core.Configuration.Validation;

/// <summary>The validation phases, in the order they run (configuration reference §14).</summary>
public enum ValidationPhase
{
    Parse = 1,
    Shape,
    Merge,
    References,
    Capabilities,
    Provider,
    Tools,
    Conditions,
    Prefix,
    Invariants,
}

/// <summary>One validation error: the setting, what is wrong, and how to fix it (CFG-06).</summary>
/// <param name="Phase">The phase that found it.</param>
/// <param name="Path">The setting, such as <c>agents.developer.model</c>.</param>
/// <param name="Problem">What is wrong, as a sentence.</param>
/// <param name="Fix">How to fix it, as a sentence.</param>
public sealed record ConfigurationError(ValidationPhase Phase, string Path, string Problem, string Fix)
{
    /// <summary>Where the setting was written, such as <c>sof.json:4:7</c>, when it came from a file.</summary>
    public string? Location { get; init; }

    public override string ToString()
    {
        var text = $"{(Path.Length == 0 ? "(configuration)" : Path)}: {Problem} {Fix}";
        return Location is null ? text : $"{text} ({Location})";
    }
}

/// <summary>Thrown when something is started with a configuration that does not validate.</summary>
public sealed class ConfigurationException : InvalidOperationException
{
    public ConfigurationException(IReadOnlyList<ConfigurationError> errors)
        : base(Describe(errors))
    {
        Errors = errors;
    }

    public ConfigurationException()
        : this([])
    {
    }

    public ConfigurationException(string message)
        : base(message)
    {
        Errors = [];
    }

    public ConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [];
    }

    public IReadOnlyList<ConfigurationError> Errors { get; } = [];

    private static string Describe(IReadOnlyList<ConfigurationError> errors) =>
        "The configuration is not valid:" + string.Concat(errors.Select(error => Environment.NewLine + "  " + error));
}
