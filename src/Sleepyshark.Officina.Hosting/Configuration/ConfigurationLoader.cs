using System.Text.Json;
using Sleepyshark.Officina.Core.Capabilities;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Configuration.Validation;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Loads a configuration from its layers, lowest to highest: code defaults, presets and extended files,
/// <c>sof.json</c>, <c>sof.&lt;environment&gt;.json</c>, <c>SOF__…</c> environment variables, and run options
/// (CFG-04). Every load reads the files again, so a changed file applies to the next run without a
/// rebuild (CFG-08). It validates in full and reports every error (CFG-06).
/// </summary>
public sealed class ConfigurationLoader
{
    private const string PresetPrefix = "preset:";

    private readonly SettingsModel model;
    private readonly PresetCatalog presets;
    private readonly IReadOnlyDictionary<string, ProviderCapabilities> providerTypes;
    private readonly ConfigurationValidator validator;

    /// <param name="capabilities">The capabilities the host makes available.</param>
    /// <param name="presets">The presets configurations may extend.</param>
    /// <param name="providerTypes">The provider types the host can create, with what each supports. When empty, provider types are not checked.</param>
    /// <param name="rules">More validation rules, for example from extensions.</param>
    public ConfigurationLoader(
        CapabilityRegistry? capabilities = null,
        PresetCatalog? presets = null,
        IReadOnlyDictionary<string, ProviderCapabilities>? providerTypes = null,
        IEnumerable<IConfigurationRule>? rules = null)
    {
        model = SettingsModel.For(capabilities ?? CapabilityRegistry.Empty);
        this.presets = presets ?? PresetCatalog.Empty;
        this.providerTypes = providerTypes ?? new Dictionary<string, ProviderCapabilities>();
        validator = new ConfigurationValidator(model, rules);
    }

    public SettingsModel Model => model;

    public LoadedConfiguration Load(ConfigurationSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var load = new LayerReader(sources, model, presets);
        load.ReadLayers();

        var defaultsTree = (ConfigObject)ConfigNode.FromJson(OptionsWriter.Write(Defaults(), model), ConfigOrigin.CodeDefault);
        ConfigNode merged = defaultsTree;
        foreach (var layer in load.Layers)
        {
            merged = LayerMerger.Merge(merged, layer.Root, defaultsTree);
        }

        var resolved = new DefinitionExtends(load.Errors, load.BadPaths).Resolve((ConfigObject)merged);
        var options = new OptionsBinder(model).Bind(resolved);
        var origins = new OriginIndex(resolved);

        var context = new ValidationContext(options, model)
        {
            DescribeProvider = name => options.Providers.TryGetValue(name, out var provider) ? providerTypes.GetValueOrDefault(provider.Type) : null,
            ProviderTypes = providerTypes.Count > 0 ? providerTypes.Keys.ToHashSet(StringComparer.Ordinal) : null,
        };
        var semanticErrors = validator.Validate(context)
            .Where(error => !load.BadPaths.Any(bad => SettingPath.IsWithin(error.Path, bad)))
            .Select(error => error with { Location = origins.LocationOf(error.Path) });

        IReadOnlyList<ConfigurationError> errors = [.. load.Errors.Concat(semanticErrors)
            .Select((error, order) => (error, order))
            .OrderBy(entry => entry.error.Phase)
            .ThenBy(entry => entry.order)
            .Select(entry => entry.error)];

        return new LoadedConfiguration(options, errors, model, origins, load.Descriptions);
    }

    /// <summary>The code defaults, with every registered capability present and off.</summary>
    private OfficinaOptions Defaults() => new()
    {
        Capabilities = new NamedMap<CapabilitySettings>(model.Capabilities.All.Select(capability => KeyValuePair.Create(capability.Name, CapabilityRegistry.DefaultSettings(capability)))),
    };

    /// <summary>One load: the layers read so far, and the errors found reading them.</summary>
    private sealed class LayerReader(ConfigurationSources sources, SettingsModel model, PresetCatalog presets)
    {
        public List<ConfigurationError> Errors { get; } = [];

        public HashSet<string> BadPaths { get; } = new(StringComparer.Ordinal);

        public List<(ConfigObject Root, string Description)> Layers { get; } = [];

        public IReadOnlyList<string> Descriptions => [.. Layers.Select(layer => layer.Description)];

        public void ReadLayers()
        {
            var directory = Path.GetFullPath(sources.Directory);
            ReadFileIfPresent(Path.Combine(directory, ConfigurationSources.MainFile), LayerKind.ApplicationFile);
            if (sources.Environment is { Length: > 0 } environment)
            {
                ReadFileIfPresent(Path.Combine(directory, $"sof.{environment}.json"), LayerKind.EnvironmentFile);
            }

            if (TextLayers.FromEnvironment(sources.EnvironmentVariables, Errors) is { } variables)
            {
                Add(variables, ignoreCase: true, "environment variables");
            }

            if (TextLayers.FromRunOptions(sources.RunOptions, Errors) is { } runOptions)
            {
                Add(runOptions, ignoreCase: true, "run options");
            }
        }

        private void ReadFileIfPresent(string fullPath, LayerKind kind)
        {
            if (File.Exists(fullPath))
            {
                ReadFile(fullPath, kind, [], null);
            }
        }

        private void ReadFile(string fullPath, LayerKind kind, List<string> chain, ConfigNode? extendsEntry)
        {
            string text;
            try
            {
                text = File.ReadAllText(fullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Errors.Add(new ConfigurationError(ValidationPhase.Parse, "", $"the file cannot be read: {exception.Message}", "Check that it exists and can be read.")
                    { Location = extendsEntry?.Origin.Location ?? Display(fullPath) });
                return;
            }

            var origin = new ConfigOrigin(kind, Display(fullPath)) { FullPath = fullPath };
            ReadText(text, origin, fullPath, chain);
        }

        private void ReadText(string text, ConfigOrigin origin, string identity, List<string> chain)
        {
            if (JsoncParser.Parse(text, origin, Errors) is not { } root || !HasKnownFormat(root))
            {
                return;
            }

            if (root.Get("extends") is ConfigArray extends)
            {
                foreach (var (entry, index) in extends.Items.Select((entry, index) => (entry, index)))
                {
                    if (entry is ConfigScalar { Kind: JsonValueKind.String } name)
                    {
                        ReadExtended(name, SettingPath.Item("extends", index), origin, [.. chain, identity]);
                    }
                }
            }

            Add(root, ignoreCase: false, $"{Describe(origin.Layer)} {origin.Source}");
        }

        private void ReadExtended(ConfigScalar entry, string path, ConfigOrigin from, List<string> chain)
        {
            var isPreset = entry.Raw.StartsWith(PresetPrefix, StringComparison.Ordinal);
            var identity = isPreset
                ? entry.Raw
                : Path.GetFullPath(Path.Combine(from.FullPath is { } containing ? Path.GetDirectoryName(containing)! : Path.GetFullPath(sources.Directory), entry.Raw));

            if (chain.Contains(identity, StringComparer.Ordinal))
            {
                var names = chain.SkipWhile(item => item != identity).Append(identity).Select(item => item.StartsWith(PresetPrefix, StringComparison.Ordinal) ? item : Display(item));
                MergeError(path, entry, $"extends forms a cycle: {string.Join(" → ", names)}.", "Remove one of the extends entries.");
                return;
            }

            if (isPreset)
            {
                var id = entry.Raw[PresetPrefix.Length..];
                if (presets.Find(id) is not { } preset)
                {
                    MergeError(path, entry, $"preset \"{id}\" does not exist.",
                        (presets.Ids.Any() ? $"Available presets: {string.Join(", ", presets.Ids)}." : "No presets are available in this version.") + Suggestions.DidYouMean(id, presets.Ids));
                    return;
                }

                ReadText(preset, new ConfigOrigin(LayerKind.Preset, entry.Raw), identity, chain);
                return;
            }

            if (!File.Exists(identity))
            {
                MergeError(path, entry, $"file \"{entry.Raw}\" does not exist.", $"Paths are relative to the file that contains them; it was looked for at {identity}.");
                return;
            }

            ReadFile(identity, LayerKind.ExtendedFile, chain, entry);
        }

        private bool HasKnownFormat(ConfigObject root)
        {
            if (root.Get("formatVersion") is not { } version
                || (version is ConfigScalar { Kind: JsonValueKind.Number } number && number.Raw == OfficinaOptions.CurrentFormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                return true;
            }

            Errors.Add(new ConfigurationError(ValidationPhase.Parse, "formatVersion", $"format version {version.ToJson()?.ToJsonString()} is not supported.",
                $"This core reads format version {OfficinaOptions.CurrentFormatVersion}.") { Location = version.Origin.Location });
            return false;
        }

        private void Add(ConfigObject root, bool ignoreCase, string description)
        {
            var checker = new LayerChecker(model, Path.GetFullPath(sources.Directory), Errors, BadPaths);
            Layers.Add((checker.Check(root, ignoreCase).Without("extends"), description));
        }

        private void MergeError(string path, ConfigNode entry, string problem, string fix)
        {
            Errors.Add(new ConfigurationError(ValidationPhase.Merge, path, problem, fix) { Location = entry.Origin.Location });
        }

        private string Display(string fullPath) => Path.GetRelativePath(Path.GetFullPath(sources.Directory), fullPath).Replace('\\', '/');

        private static string Describe(LayerKind kind) => kind switch
        {
            LayerKind.Preset => "preset",
            LayerKind.ExtendedFile => "extended file",
            LayerKind.ApplicationFile => "application file",
            _ => "environment file",
        };
    }
}
