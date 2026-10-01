using System.Reflection;
using Sleepyshark.Officina.Cli.Commands;

namespace Sleepyshark.Officina.Cli;

/// <summary>The <c>sof</c> command line.</summary>
public static class SofCommandLine
{
    private const string Usage = """
        Usage: sof <command>

        Commands:
          config show | validate | dry-run   Show, check or try out the configuration. See sof config --help.
          --version                          Print the version.
        """;

    public static string Version { get; } =
        typeof(SofCommandLine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, SofConsole console, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);
        try
        {
            switch (args.ToArray())
            {
                case ["--version"]:
                    console.Out.WriteLine(Version);
                    return ExitCodes.Success;
                case ["config", .. var rest]:
                    return await new ConfigCommand(console).RunAsync(rest, ct).ConfigureAwait(false);
                case [] or ["--help"] or ["-h"] or ["help"]:
                    console.Out.WriteLine($"sof {Version} - the Officina coding team CLI.");
                    console.Out.WriteLine(Usage);
                    return args.Count == 0 ? ExitCodes.Usage : ExitCodes.Success;
                default:
                    throw new UsageException($"Unknown command \"{string.Join(' ', args)}\".");
            }
        }
        catch (Exception exception) when (exception is UsageException or ArgumentException)
        {
            console.Error.WriteLine($"error: {exception.Message}");
            console.Error.WriteLine("Run sof --help for usage.");
            return ExitCodes.Usage;
        }
    }
}
