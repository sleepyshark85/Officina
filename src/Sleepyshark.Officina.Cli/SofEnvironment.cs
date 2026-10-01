namespace Sleepyshark.Officina.Cli;

/// <summary>What the CLI reads and writes outside itself (its process environment), so tests can run it in-process.</summary>
/// <param name="Out">Standard output.</param>
/// <param name="Error">Standard error.</param>
/// <param name="WorkingDirectory">The current directory.</param>
/// <param name="Variables">The environment variables.</param>
public sealed record SofEnvironment(TextWriter Out, TextWriter Error, string WorkingDirectory, IReadOnlyDictionary<string, string> Variables);
