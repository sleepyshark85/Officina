using System.CommandLine;

namespace Sleepyshark.Officina.Cli;

/// <summary><c>sof config show [--origin]</c>: every effective setting, and with <c>--origin</c> where each came from (CFG-04).</summary>
internal static class ShowCommand
{
    private const int ValueWidth = 72;

    public static Command Create(ConfigurationCommandOptions shared, SofEnvironment host)
    {
        var origin = new Option<bool>("--origin") { Description = "Show where each value came from." };
        var command = new Command("show", "Print the effective configuration.") { origin };
        shared.AddTo(command);
        command.SetAction(parse =>
        {
            var configuration = shared.Load(parse, host);
            Print(configuration, host.Out, parse.GetValue(origin));
            return ConfigurationCommandOptions.ReportErrors(configuration, host);
        });
        return command;
    }

    /// <summary>Prints every effective setting, one per line, with its source when <paramref name="origin"/> is set.</summary>
    public static void Print(SofConfiguration configuration, TextWriter output, bool origin)
    {
        var settings = configuration.Settings();
        var pathWidth = settings.Max(setting => setting.Path.Length);
        var valueWidth = settings.Max(setting => Shorten(setting.Value).Length);
        foreach (var (path, value) in settings)
        {
            var line = $"{path.PadRight(pathWidth)}  {Shorten(value)}";
            output.WriteLine(origin ? $"{line.PadRight(pathWidth + valueWidth + 2)}  {configuration.SourceOf(path)}" : line);
        }
    }

    private static string Shorten(string value) => value.Length <= ValueWidth ? value : $"{value[..(ValueWidth - 14)]}… ({value.Length} chars)";
}
