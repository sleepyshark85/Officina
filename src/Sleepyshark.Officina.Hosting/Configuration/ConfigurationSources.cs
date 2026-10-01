namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>Where a configuration is loaded from (configuration reference §2, §13).</summary>
public sealed record ConfigurationSources
{
    public const string MainFile = "sof.json";

    /// <summary>The variable that selects the environment when the host gives none.</summary>
    public const string EnvironmentVariable = "SOF_ENVIRONMENT";

    /// <summary>The directory that holds <c>sof.json</c>.</summary>
    public required string Directory { get; init; }

    /// <summary>The environment whose <c>sof.&lt;environment&gt;.json</c> is merged on top, or null.</summary>
    public string? Environment { get; init; }

    /// <summary>The environment variables; those named <c>SOF__…</c> are a layer.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>What the host passes when it starts a run, such as the CLI's <c>--budget</c>.</summary>
    public IReadOnlyList<RunOption> RunOptions { get; init; } = [];
}

/// <summary>A setting the host passes for a run.</summary>
/// <param name="Path">The setting, as dotted names, such as <c>run.budget.cost</c>.</param>
/// <param name="Value">The value: JSON where it parses as JSON, otherwise text.</param>
/// <param name="Source">What set it, such as <c>--budget</c>, shown as its origin.</param>
public sealed record RunOption(string Path, string Value, string Source);
