using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Cli;

/// <summary>What the CLI reads and writes outside itself (its process environment), so tests can run it in-process.</summary>
/// <param name="Out">Standard output.</param>
/// <param name="Error">Standard error.</param>
/// <param name="WorkingDirectory">The current directory.</param>
/// <param name="Variables">The environment variables.</param>
public sealed record SofEnvironment(TextWriter Out, TextWriter Error, string WorkingDirectory, IReadOnlyDictionary<string, string> Variables)
{
    /// <summary>Standard input, where the owner types commands during <c>sof run</c>.</summary>
    public TextReader In { get; init; } = TextReader.Null;

    /// <summary>The model providers <c>sof run</c> can use, by their name in <c>providers</c>.</summary>
    public IReadOnlyDictionary<string, IModelProvider> Providers { get; init; } = new Dictionary<string, IModelProvider>();

    /// <summary>Where commands run; the machine's own sandbox when null (SBX-07).</summary>
    public ISandbox? Sandbox { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}
