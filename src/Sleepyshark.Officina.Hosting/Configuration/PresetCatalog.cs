namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Presets a configuration can extend as <c>preset:&lt;id&gt;</c> (configuration reference §15). A preset is
/// an ordinary configuration file, used as a lower layer. The v1 presets ship with the team slice (CFG-11).
/// </summary>
public sealed class PresetCatalog
{
    private readonly SortedDictionary<string, string> presets = new(StringComparer.Ordinal);

    public static PresetCatalog Empty { get; } = new();

    public IEnumerable<string> Ids => presets.Keys;

    /// <param name="id">The id, used as <c>preset:&lt;id&gt;</c>.</param>
    /// <param name="json">The preset's configuration text.</param>
    public PresetCatalog Add(string id, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(json);
        presets.Add(id, json);
        return this;
    }

    public string? Find(string id) => presets.GetValueOrDefault(id);
}
