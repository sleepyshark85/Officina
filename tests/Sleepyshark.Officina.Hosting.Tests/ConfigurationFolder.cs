using Sleepyshark.Officina.Hosting.Configuration;

namespace Sleepyshark.Officina.Hosting.Tests;

/// <summary>A real directory with configuration files, loaded with the real loader.</summary>
internal sealed class ConfigurationFolder : IDisposable
{
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("officina-config-").FullName;

    public ConfigurationFolder Write(string relativePath, string text)
    {
        var path = Path.Combine(Directory, relativePath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return this;
    }

    public LoadedConfiguration Load(string? environment = null, Dictionary<string, string>? variables = null, params RunOption[] runOptions) =>
        ConfigurationLoader.Load(new ConfigurationSources
        {
            Directory = Directory,
            Environment = environment,
            EnvironmentVariables = variables ?? [],
            RunOptions = runOptions,
        });

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}
