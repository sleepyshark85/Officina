using System.Reflection;
using Sleepyshark.Officina.Hosting.Configuration;

namespace Sleepyshark.Officina.Cli;

/// <summary>The <c>sof</c> command line: for now <c>config show</c> and <c>config validate</c> (CFG-04, CFG-06).</summary>
public static class SofCommandLine
{
    private const string Usage = """
        Usage:
          sof config show [--agent <name>] [--origin]   Print the effective configuration, and with --origin where each value came from.
          sof config validate                           Validate the configuration; exits with 1 when it has errors.
          sof --version

        Options of the config commands:
          --dir <path>              The directory with sof.json (default: the current directory).
          --environment <name>      Also merge sof.<name>.json (default: the SOF_ENVIRONMENT variable).
          --budget <amount>         Run option: run.budget.cost.
          --permission-mode <mode>  Run option: run.permissionMode (ask, auto or readOnly).
        """;

    private static readonly string[] ValueOptions = ["--dir", "--environment", "--agent", "--budget", "--permission-mode"];

    public static string Version { get; } =
        typeof(SofCommandLine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static int Run(IReadOnlyList<string> args, SofConsole console)
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
                case ["config", var command, .. var rest] when command is "show" or "validate":
                    return Config(command, Options(rest), console);
                default:
                    console.Error.WriteLine(Usage);
                    return ExitCodes.Usage;
            }
        }
        catch (ArgumentException exception)
        {
            console.Error.WriteLine($"error: {exception.Message}");
            return ExitCodes.Usage;
        }
    }

    private static int Config(string command, Dictionary<string, string> options, SofConsole console)
    {
        var runOptions = new List<RunOption>();
        if (options.TryGetValue("--budget", out var budget))
        {
            runOptions.Add(new RunOption("run.budget.cost", budget, "--budget"));
        }

        if (options.TryGetValue("--permission-mode", out var mode))
        {
            runOptions.Add(new RunOption("run.permissionMode", mode, "--permission-mode"));
        }

        var configuration = ConfigurationLoader.Load(new ConfigurationSources
        {
            Directory = Path.GetFullPath(options.GetValueOrDefault("--dir") ?? ".", console.WorkingDirectory),
            Environment = options.GetValueOrDefault("--environment") ?? console.EnvironmentVariables.GetValueOrDefault(ConfigurationSources.EnvironmentVariable),
            EnvironmentVariables = console.EnvironmentVariables,
            RunOptions = runOptions,
        });

        if (command == "show")
        {
            var settings = configuration.Settings(options.GetValueOrDefault("--agent"));
            var pathWidth = settings.Max(setting => setting.Path.Length);
            var valueWidth = settings.Max(setting => Shorten(setting.Value).Length);
            foreach (var setting in settings)
            {
                console.Out.WriteLine(options.ContainsKey("--origin")
                    ? $"{setting.Path.PadRight(pathWidth)}  {Shorten(setting.Value).PadRight(valueWidth)}  {setting.Origin}"
                    : $"{setting.Path.PadRight(pathWidth)}  {Shorten(setting.Value)}");
            }
        }

        foreach (var error in configuration.Errors)
        {
            console.Error.WriteLine($"error: {error}");
        }

        if (!configuration.IsValid)
        {
            console.Error.WriteLine(configuration.Errors.Count == 1 ? "1 error." : $"{configuration.Errors.Count} errors.");
            return ExitCodes.Invalid;
        }

        if (command == "validate")
        {
            console.Out.WriteLine($"The configuration is valid (code defaults{string.Concat(configuration.Layers.Select(layer => ", " + layer))}).");
        }

        return ExitCodes.Success;
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--origin")
            {
                options["--origin"] = "";
            }
            else if (ValueOptions.Contains(args[index]) && index + 1 < args.Length)
            {
                options[args[index]] = args[++index];
            }
            else
            {
                throw new ArgumentException($"Unknown option \"{args[index]}\". Run sof without arguments for usage.");
            }
        }

        return options;
    }

    private static string Shorten(string value) => value.Length <= 72 ? value : $"{value[..58]}… ({value.Length} chars)";
}
