using System.CommandLine;

namespace Sleepyshark.Officina.Cli;

/// <summary><c>sof config show [--agent &lt;name&gt;] [--origin]</c>: the effective configuration, and where each value came from (CFG-04).</summary>
internal static class ShowCommand
{
    private const int ValueWidth = 72;

    public static Command Create(ConfigurationCommandOptions shared, SofEnvironment host)
    {
        var agent = new Option<string>("--agent") { Description = "Show only the settings that apply to this agent." };
        var origin = new Option<bool>("--origin") { Description = "Show where each value came from." };
        var command = new Command("show", "Print the effective configuration.") { agent, origin };
        shared.AddTo(command);
        command.SetAction(parse =>
        {
            var configuration = shared.Load(parse, host);
            var settings = configuration.Settings(parse.GetValue(agent));
            var pathWidth = settings.Max(setting => setting.Path.Length);
            var valueWidth = settings.Max(setting => Shorten(setting.Value).Length);
            foreach (var setting in settings)
            {
                var path = setting.Path.PadRight(pathWidth);
                var value = Shorten(setting.Value);
                host.Out.WriteLine(parse.GetValue(origin) ? $"{path}  {value.PadRight(valueWidth)}  {setting.Origin}" : $"{path}  {value}");
            }

            return ConfigurationCommandOptions.ReportErrors(configuration, host);
        });
        return command;
    }

    private static string Shorten(string value) => value.Length <= ValueWidth ? value : $"{value[..(ValueWidth - 14)]}… ({value.Length} chars)";
}
