namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>A real directory with configuration files, loaded with the CLI's real loader.</summary>
internal sealed class ConfigurationFolder : IDisposable
{
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("officina-config-").FullName;

    public ConfigurationFolder Write(string relativePath, string text)
    {
        File.WriteAllText(Path.Combine(Directory, relativePath), text);
        return this;
    }

    public SofConfiguration Load(string? environment = null, Dictionary<string, string>? variables = null, params (string, string, string)[] options) =>
        SofConfiguration.Load(Directory, environment, variables ?? [], options);

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}
