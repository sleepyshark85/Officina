using System.Runtime.InteropServices;
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

    /// <summary>
    /// Takes Ctrl+C (SIGINT) and SIGTERM over from the command line until the registration it returns is disposed: in <c>sof chat</c>,
    /// Ctrl+C cancels the reply and keeps the session. Null leaves both to the command line, which cancels the command.
    /// </summary>
    public Func<Action<PosixSignal>, IDisposable>? Signals { get; init; }

    /// <summary>Whether the command runs inside a chat session, typed after a <c>/</c>: the session handles the signals, and holds the console.</summary>
    internal bool InSession { get; init; }

    /// <summary>The interactive terminal, where a chat session reads lines with a line editor; null elsewhere, and in tests.</summary>
    internal TerminalScreen? Terminal { get; init; }

    /// <summary>
    /// Whether the owner types at a terminal, so a chat session may ask them something, such as whether to save a document; with
    /// piped input, nothing is asked. By default, whether there is a <see cref="Terminal"/>.
    /// </summary>
    internal bool Interactive
    {
        get => interactive ?? Terminal is not null;
        init => interactive = value;
    }

    private readonly bool? interactive;

    /// <summary>The process's own signals, for <see cref="Signals"/>.</summary>
    public static IDisposable ProcessSignals(Action<PosixSignal> handler) => new Registrations(
        [.. new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM }.Select(signal => PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            handler(context.Signal);
        }))]);

    private sealed class Registrations(List<PosixSignalRegistration> registrations) : IDisposable
    {
        public void Dispose() => registrations.ForEach(registration => registration.Dispose());
    }
}
