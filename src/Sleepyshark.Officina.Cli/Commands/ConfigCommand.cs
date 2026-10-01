using System.Globalization;
using Sleepyshark.Officina.Core.Configuration.Validation;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Hosting.Configuration;

namespace Sleepyshark.Officina.Cli.Commands;

/// <summary><c>sof config show | validate | dry-run</c> (CFG-04, CFG-06, CFG-12).</summary>
internal sealed class ConfigCommand(SofConsole console)
{
    public const string Usage = """
        Usage:
          sof config show [--agent <name>] [--origin]   Print the effective configuration, and with --origin where each value came from.
          sof config validate                           Validate the configuration; exits with 1 when it has errors.
          sof config dry-run [--agent <name>] [--input <text>] [--reply <text>]...
                                                        Validate, show the agent, and run it against a scripted model.

        Options for every config command:
          --dir <path>              The directory with sof.json (default: the current directory).
          --environment <name>      Also merge sof.<name>.json (default: the SOF_ENVIRONMENT variable).
          --set <setting>=<value>   Set a run option, such as --set run.permissionMode=auto.
          --budget <amount>         The run budget, as run.budget.cost.
          --permission-mode <mode>  The permission mode: ask, auto or readOnly.
        """;

    private const int ValueWidth = 72;

    private static readonly HashSet<string> ValueOptions = ["--dir", "--environment", "--agent", "--set", "--budget", "--permission-mode", "--input", "--reply"];
    private static readonly HashSet<string> FlagOptions = ["--origin"];

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
        {
            console.Out.WriteLine(Usage);
            return args.Count == 0 ? ExitCodes.Usage : ExitCodes.Success;
        }

        var arguments = CommandArguments.Parse(args.Skip(1), ValueOptions, FlagOptions);
        var configuration = SofApplication.CreateLoader().Load(Sources(arguments));
        return args[0] switch
        {
            "show" => Show(configuration, arguments),
            "validate" => Validate(configuration),
            "dry-run" => await DryRunAsync(configuration, arguments, ct).ConfigureAwait(false),
            _ => throw new UsageException($"Unknown config command \"{args[0]}\". Use show, validate or dry-run."),
        };
    }

    private ConfigurationSources Sources(CommandArguments arguments)
    {
        var runOptions = new List<RunOption>();
        if (arguments.Value("--budget") is { } budget)
        {
            runOptions.Add(new RunOption("run.budget.cost", budget, "--budget"));
        }

        if (arguments.Value("--permission-mode") is { } mode)
        {
            runOptions.Add(new RunOption("run.permissionMode", mode, "--permission-mode"));
        }

        foreach (var assignment in arguments.Values("--set"))
        {
            var equals = assignment.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new UsageException($"--set takes <setting>=<value>, such as run.permissionMode=auto, not \"{assignment}\".");
            }

            runOptions.Add(new RunOption(assignment[..equals], assignment[(equals + 1)..], "--set " + assignment[..equals]));
        }

        var directory = Path.GetFullPath(arguments.Value("--dir") ?? console.WorkingDirectory, console.WorkingDirectory);
        var environment = arguments.Value("--environment")
            ?? (console.EnvironmentVariables.TryGetValue(ConfigurationSources.EnvironmentVariable, out var selected) && selected.Length > 0 ? selected : null);
        return new ConfigurationSources
        {
            Directory = directory,
            Environment = environment,
            EnvironmentVariables = console.EnvironmentVariables,
            RunOptions = runOptions,
        };
    }

    private int Show(LoadedConfiguration configuration, CommandArguments arguments)
    {
        var settings = configuration.Settings(arguments.Value("--agent"));
        var withOrigin = arguments.Has("--origin");
        var pathWidth = settings.Max(setting => setting.Path.Length);
        foreach (var setting in settings)
        {
            var value = Shorten(setting.Value);
            console.Out.WriteLine(withOrigin
                ? $"{setting.Path.PadRight(pathWidth)}  {value.PadRight(Math.Min(ValueWidth, settings.Max(other => Shorten(other.Value).Length)))}  {setting.Origin}"
                : $"{setting.Path.PadRight(pathWidth)}  {value}");
        }

        return ReportErrors(configuration);
    }

    private int Validate(LoadedConfiguration configuration)
    {
        if (ReportErrors(configuration) == ExitCodes.Invalid)
        {
            return ExitCodes.Invalid;
        }

        var layers = configuration.Layers.Count == 0 ? "code defaults only; no sof.json found" : "code defaults, " + string.Join(", ", configuration.Layers);
        console.Out.WriteLine($"The configuration is valid ({layers}).");
        return ExitCodes.Success;
    }

    private async Task<int> DryRunAsync(LoadedConfiguration configuration, CommandArguments arguments, CancellationToken ct)
    {
        if (ReportErrors(configuration) == ExitCodes.Invalid)
        {
            return ExitCodes.Invalid;
        }

        var options = configuration.Options;
        var agent = arguments.Value("--agent") ?? (options.Agents.Count == 1 ? options.Agents.Keys.Single() : null)
            ?? throw new UsageException(options.Agents.Count == 0
                ? "The configuration has no agents to run."
                : $"Choose the agent with --agent: {string.Join(", ", options.Agents.Keys)}.");

        console.Out.WriteLine($"Agent {agent}:");
        foreach (var setting in configuration.Settings(agent))
        {
            console.Out.WriteLine($"  {setting.Path} = {Shorten(setting.Value)}");
        }

        // A scripted model stands in for every provider, so nothing is sent to a real model (CFG-12).
        var model = new DryRunModel(arguments.Values("--reply"));
        var providers = options.Providers.Keys.ToDictionary(name => name, IModelProvider (_) => model);
        var runs = new InMemoryRunStore();
        var runner = new AgentRunner(options, providers, SofApplication.Capabilities, runs);
        var input = arguments.Value("--input") ?? "(dry run input)";
        var result = await runner.RunAsync(agent, input, ct).ConfigureAwait(false);

        var request = model.Requests.Single();
        console.Out.WriteLine();
        console.Out.WriteLine($"Model call to {request.Profile.Provider}/{request.Profile.Model} (scripted):");
        console.Out.WriteLine("  Instructions:");
        foreach (var line in request.Instructions.Split('\n'))
        {
            console.Out.WriteLine("    " + line);
        }

        console.Out.WriteLine($"  Input: {input}");
        console.Out.WriteLine($"Result: {result.Outcome.ToString().ToLowerInvariant()}");
        console.Out.WriteLine($"  Output: {result.Output}");
        var run = runs.Runs.Single();
        console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Run {run.RunId} recorded its configuration (sha256 {run.Configuration.Sha256[..12]}, core {run.Configuration.CoreVersion})."));
        return ExitCodes.Success;
    }

    private int ReportErrors(LoadedConfiguration configuration)
    {
        foreach (var error in configuration.Errors)
        {
            console.Error.WriteLine($"error: {Format(error)}");
        }

        if (configuration.Errors.Count > 0)
        {
            console.Error.WriteLine(configuration.Errors.Count == 1 ? "1 error." : $"{configuration.Errors.Count} errors.");
            return ExitCodes.Invalid;
        }

        return ExitCodes.Success;
    }

    private static string Format(ConfigurationError error) =>
        error.Location is { } location ? $"{location}: {error with { Location = null }}" : error.ToString();

    private static string Shorten(string value) =>
        value.Length <= ValueWidth ? value : string.Create(CultureInfo.InvariantCulture, $"{value[..(ValueWidth - 14)]}… ({value.Length} chars)");
}
