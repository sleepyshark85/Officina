using System.Collections;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>Where a configuration is loaded from (configuration reference §2, §13).</summary>
public sealed record ConfigurationSources
{
    /// <summary>The main file's name.</summary>
    public const string MainFile = "sof.json";

    /// <summary>The environment variable that selects the environment when none is given.</summary>
    public const string EnvironmentVariable = "SOF_ENVIRONMENT";

    /// <summary>The directory that holds <c>sof.json</c>: the application's directory, or for the coding team CLI the project root.</summary>
    public required string Directory { get; init; }

    /// <summary>The environment whose file <c>sof.&lt;environment&gt;.json</c> is merged on top, or null for none.</summary>
    public string? Environment { get; init; }

    /// <summary>The environment variables; those named <c>SOF__…</c> are a layer.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>What the host passes when it starts a run, such as the CLI's <c>--budget</c>.</summary>
    public IReadOnlyList<RunOption> RunOptions { get; init; } = [];

    /// <summary>The sources of this process: its environment variables, and the environment they select unless one is given.</summary>
    public static ConfigurationSources ForProcess(string directory, string? environment = null, IReadOnlyList<RunOption>? runOptions = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry variable in System.Environment.GetEnvironmentVariables())
        {
            variables[(string)variable.Key] = (string?)variable.Value ?? "";
        }

        return new ConfigurationSources
        {
            Directory = directory,
            Environment = environment ?? (variables.TryGetValue(EnvironmentVariable, out var selected) && selected.Length > 0 ? selected : null),
            EnvironmentVariables = variables,
            RunOptions = runOptions ?? [],
        };
    }
}
