namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>One effective setting: its path, its value as JSON, and where it came from (CFG-04).</summary>
public sealed record EffectiveSetting(string Path, string Value, ConfigurationOrigin Origin);
