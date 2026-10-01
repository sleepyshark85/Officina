using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Loads a configuration with Microsoft.Extensions.Configuration (CFG-04): <c>sof.json</c>, the optional
/// <c>sof.&lt;environment&gt;.json</c>, <c>SOF__…</c> environment variables and run options, over the code defaults.
/// It resolves <c>extends</c> between agent definitions, rejects what is not a setting, binds the Options classes and
/// validates them in full (CFG-06). Every load reads the files again, so a changed file applies to the next run
/// without a rebuild (CFG-08).
/// </summary>
public static class ConfigurationLoader
{
    public static LoadedConfiguration Load(ConfigurationSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var errors = new List<ConfigurationError>();
        var layers = new ConfigurationLayers(sources, errors);

        var direct = layers.Build();
        var extends = new DefinitionExtends(direct, key => layers.OriginOf(direct, key)).Resolve();
        errors.AddRange(extends.Errors);

        var configuration = layers.Build(extends.Inherited);
        ConfigurationOrigin? SetBy(string key) => layers.OriginOf(configuration, key) ?? extends.Origins.GetValueOrDefault(key);

        // Where an error is: the setting, or the nearest section around it that a layer wrote.
        string? LocationOf(string key)
        {
            for (var names = key.Split(':'); names.Length > 0; names = names[..^1])
            {
                if (SetBy(string.Join(':', names)) is { } origin)
                {
                    return origin.Location;
                }
            }

            return null;
        }

        var supported = OfficinaOptions.CurrentFormatVersion.ToString(CultureInfo.InvariantCulture);
        if (configuration["formatVersion"] is { } version && version != supported)
        {
            errors.Add(new ConfigurationError(ValidationPhase.Parse, "formatVersion", $"format version {version} is not supported.",
                $"This core reads format version {supported}.") { Location = LocationOf("formatVersion") });
        }

        errors.AddRange(UnknownSettings.Check(configuration, SetBy));
        var options = Bind(configuration, errors);

        // Rules that also apply to the programmatic form.
        var semantic = options.Validate().Select(error => error with { Location = LocationOf(error.Path.Replace('.', ':')) });
        var all = errors.Concat(semantic).OrderBy(error => error.Phase).ToArray();
        return new LoadedConfiguration(options, all, path => SetBy(path.Replace('.', ':')) ?? ConfigurationOrigin.CodeDefault, layers.Descriptions);
    }

    /// <summary>
    /// Binds the Options classes. The binder stops at the first value it cannot convert, and its message names the
    /// setting; inside named entries it skips such values silently, so each entry is also bound on its own to report them.
    /// </summary>
    private static OfficinaOptions Bind(IConfiguration configuration, List<ConfigurationError> errors)
    {
        foreach (var property in typeof(OfficinaOptions).GetProperties().Where(IsNamedSection))
        {
            var entryType = property.PropertyType.GetGenericArguments()[1];
            foreach (var entry in configuration.GetSection(JsonNamingPolicy.CamelCase.ConvertName(property.Name)).GetChildren())
            {
                TryBind(() => entry.Get(entryType), errors, entry.Path.Replace(':', '.'));
            }
        }

        return TryBind(() => configuration.Get<OfficinaOptions>(), errors, "") ?? new OfficinaOptions();
    }

    // path: the section being bound; the binder's message names the setting within it.
    private static T? TryBind<T>(Func<T?> bind, List<ConfigurationError> errors, string path)
    {
        try
        {
            return bind();
        }
        catch (InvalidOperationException exception)
        {
            // A value already reported as not fitting its setting is not reported again by the binder.
            if (path.Length == 0 || !errors.Any(error => error.Path.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase)))
            {
                var fix = "Correct the value; until then, its default applies.";
                errors.Add(new ConfigurationError(ValidationPhase.Shape, path, exception.Message, fix));
            }

            return default;
        }
    }

    private static bool IsNamedSection(System.Reflection.PropertyInfo property) =>
        property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>);
}
