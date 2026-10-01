namespace Sleepyshark.Officina.Cli;

/// <summary>What a command reads and writes outside itself, so tests can run the CLI in-process.</summary>
public sealed record SofConsole(TextWriter Out, TextWriter Error, string WorkingDirectory, IReadOnlyDictionary<string, string> EnvironmentVariables);

public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>The configuration has errors.</summary>
    public const int Invalid = 1;

    /// <summary>The command line is wrong.</summary>
    public const int Usage = 2;
}
