using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Sleepyshark.Officina.Core;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The configuration the CLI runs with (CFG-04): <c>sof.json</c>, the optional <c>sof.&lt;environment&gt;.json</c>,
/// <c>SOF__…</c> environment variables and command-line options, highest last, bound onto <see cref="OfficinaOptions"/>
/// with Microsoft.Extensions.Configuration. Every load reads the files again, so a change applies to the next run
/// without a rebuild (CFG-08).
/// </summary>
public sealed class SofConfiguration
{
    private const string VariablePrefix = "SOF__";

    private readonly IConfigurationRoot? root;
    private readonly List<Func<string, string>> describe;

    private SofConfiguration(
        OfficinaOptions options, IReadOnlyList<ConfigurationError> errors, IConfigurationRoot? root, List<Func<string, string>> describe)
    {
        Options = options;
        Errors = errors;
        this.root = root;
        this.describe = describe;
    }

    public OfficinaOptions Options { get; }

    /// <summary>Errors in loading the files or binding the values, and every validation error.</summary>
    public IReadOnlyList<ConfigurationError> Errors { get; }

    /// <param name="directory">The directory that holds <c>sof.json</c>.</param>
    /// <param name="environment">The environment whose <c>sof.&lt;environment&gt;.json</c> is merged on top, or null.</param>
    /// <param name="variables">The environment variables; those named <c>SOF__…</c> are a layer. Passed in so a test decides them.</param>
    /// <param name="options">Settings given on the command line, by setting path, with the option that set each.</param>
    public static SofConfiguration Load(
        string directory,
        string? environment,
        IReadOnlyDictionary<string, string> variables,
        IEnumerable<(string Path, string Value, string Option)> options)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var builder = new ConfigurationBuilder().SetBasePath(directory).AddJsonFile("sof.json");
        var describe = new List<Func<string, string>> { _ => "sof.json" };
        if (environment is { Length: > 0 })
        {
            builder.AddJsonFile($"sof.{environment}.json", optional: true);
            describe.Add(_ => $"sof.{environment}.json");
        }

        AddText(builder, describe, variables
            .Where(variable => variable.Key.StartsWith(VariablePrefix, StringComparison.Ordinal))
            .Select(variable => (
                variable.Key[VariablePrefix.Length..].Replace("__", ":", StringComparison.Ordinal),
                variable.Value,
                $"environment variable {variable.Key}")));
        AddText(builder, describe, options.Select(option => (option.Path.Replace('.', ':'), option.Value, $"option {option.Option}")));

        IConfigurationRoot root;
        try
        {
            root = builder.Build();
        }
        catch (Exception exception) when (exception is InvalidDataException or FileNotFoundException)
        {
            var reason = exception.InnerException?.Message ?? exception.Message;
            var error = new ConfigurationError(
                ValidationPhase.Parse, "", $"the configuration cannot be read: {reason}", "Fix the file; comments and trailing commas are allowed.");
            return new SofConfiguration(new OfficinaOptions(), [error], null, describe);
        }

        var errors = new List<ConfigurationError>();
        OfficinaOptions bound;
        try
        {
            bound = root.Get<OfficinaOptions>() ?? new OfficinaOptions();
        }
        catch (InvalidOperationException exception)
        {
            // The binder stops at the first value it cannot convert; its message names the setting.
            errors.Add(new ConfigurationError(ValidationPhase.Shape, "", exception.Message, "Correct the value; until then, the defaults apply."));
            bound = new OfficinaOptions();
        }

        var configuration = new SofConfiguration(bound, errors, root, describe);
        errors.AddRange(PlainTextSecrets(root).Select(error => error with { Location = configuration.Provided(error.Path) }));
        errors.AddRange(bound.Validate().Select(error => error with { Location = configuration.Provided(error.Path) }));
        return configuration;
    }

    /// <summary>Where a setting's value came from: the highest layer that sets it, or the code default.</summary>
    /// <param name="path">The setting, such as <c>run.budget.cost</c>.</param>
    public string SourceOf(string path) => Provided(path) ?? $"code default, core {CoreVersion.Value}";

    /// <summary>Every effective setting, defaults included, with its value as JSON.</summary>
    public IReadOnlyList<(string Path, string Value)> Settings()
    {
        var settings = new List<(string, string)>();
        Flatten(JsonSerializer.SerializeToNode(Options, ConfigurationJson.Options)!, "", settings);
        return settings;
    }

    private string? Provided(string path)
    {
        var providers = root?.Providers.ToArray() ?? [];
        var key = path.Replace('.', ':');
        for (var index = providers.Length - 1; index >= 0; index--)
        {
            if (providers[index].TryGet(key, out _))
            {
                return describe[index](key);
            }
        }

        return null;
    }

    /// <summary>
    /// A secret written as a value instead of a reference (CFG-09). The binder skips a value where a section belongs, and
    /// the default reference would apply in its place, so it is reported, without repeating the value.
    /// </summary>
    private static IEnumerable<ConfigurationError> PlainTextSecrets(IConfigurationRoot root) =>
        root.GetSection("providers").GetChildren()
            .Where(provider => provider.GetSection("apiKey").Value is { Length: > 0 })
            .Select(provider => new ConfigurationError(
                ValidationPhase.Shape,
                $"providers.{provider.Key}.apiKey",
                "is a value, but it must reference a secret.",
                "Write { \"secret\": \"NAME\" } and put the value in the secret source, such as an environment variable NAME."));

    /// <summary>Environment variables or command-line options, as one in-memory layer that remembers what set each key.</summary>
    private static void AddText(
        IConfigurationBuilder builder, List<Func<string, string>> describe, IEnumerable<(string Key, string Value, string Source)> settings)
    {
        var sourceOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value, source) in settings)
        {
            values[key] = value;
            sourceOf[key] = source;
        }

        builder.AddInMemoryCollection(values);
        describe.Add(key => sourceOf[key]);
    }

    private static void Flatten(JsonNode node, string path, List<(string, string)> settings)
    {
        if (node is JsonObject section && section.Count > 0)
        {
            foreach (var (name, value) in section)
            {
                Flatten(value!, path.Length == 0 ? name : $"{path}.{name}", settings);
            }

            return;
        }

        settings.Add((path, node.ToJsonString(ConfigurationJson.Options)));
    }
}
