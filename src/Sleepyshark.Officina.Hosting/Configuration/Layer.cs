using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>One configuration layer: its settings as JSON, and where each of them was written.</summary>
/// <param name="Description">The layer as <c>sof config validate</c> lists it, such as <c>application file sof.json</c>.</param>
/// <param name="Origin">The layer itself, such as the file, without a position.</param>
/// <param name="Root">The layer's settings, without the keys that exist only in files.</param>
/// <param name="Positions">Where each part of the layer was written, by setting path.</param>
internal sealed record Layer(string Description, ConfigOrigin Origin, JsonObject Root, IReadOnlyDictionary<string, ConfigOrigin> Positions)
{
    /// <summary>The origin of a value of this layer; a value inside one written as a whole takes the enclosing origin.</summary>
    public ConfigOrigin OriginOf(string path)
    {
        for (var current = path; ; current = SettingPaths.Parent(current))
        {
            if (Positions.TryGetValue(current, out var origin))
            {
                return origin;
            }

            if (current.Length == 0)
            {
                return Origin;
            }
        }
    }
}
