using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// The errors found while loading, and the settings they rejected, so a later phase does not report the same
/// setting again (CFG-06).
/// </summary>
internal sealed class ConfigurationErrors
{
    private readonly List<ConfigurationError> errors = [];
    private readonly HashSet<string> rejected = new(StringComparer.Ordinal);

    public IReadOnlyList<ConfigurationError> All => errors;

    public void Add(ValidationPhase phase, string path, string problem, string fix, ConfigOrigin? at)
    {
        errors.Add(new ConfigurationError(phase, path, problem, fix) { Location = at?.Location });
        if (path.Length > 0)
        {
            rejected.Add(path);
        }
    }

    /// <summary>Rejects a setting whose error is reported elsewhere.</summary>
    public void Reject(string path) => rejected.Add(path);

    public bool IsRejected(string path) => rejected.Any(bad => SettingPaths.IsWithin(path, bad));
}
