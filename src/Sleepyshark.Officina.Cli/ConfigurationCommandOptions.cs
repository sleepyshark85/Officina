using System.CommandLine;
using Sleepyshark.Officina.Hosting.Configuration;

namespace Sleepyshark.Officina.Cli;

/// <summary>What the config commands share: the options that choose the configuration, loading it, and printing its errors.</summary>
internal sealed class ConfigurationCommandOptions
{
    private readonly Option<string> directory = new("--dir") { Description = "The directory with sof.json (default: the current directory)." };
    private readonly Option<string> environment = new("--environment")
    {
        Description = "Also merge sof.<name>.json (default: the SOF_ENVIRONMENT variable).",
    };
    private readonly Option<string> budget = new("--budget") { Description = "Run option: run.budget.cost." };
    private readonly Option<string> permissionMode = new("--permission-mode")
    {
        Description = "Run option: run.permissionMode (ask, auto or readOnly).",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(directory);
        command.Options.Add(environment);
        command.Options.Add(budget);
        command.Options.Add(permissionMode);
    }

    public LoadedConfiguration Load(ParseResult parse, SofEnvironment host)
    {
        var runOptions = new List<RunOption>();
        if (parse.GetValue(budget) is { } cost)
        {
            runOptions.Add(new RunOption("run.budget.cost", cost, "--budget"));
        }

        if (parse.GetValue(permissionMode) is { } mode)
        {
            runOptions.Add(new RunOption("run.permissionMode", mode, "--permission-mode"));
        }

        return ConfigurationLoader.Load(new ConfigurationSources
        {
            Directory = Path.GetFullPath(parse.GetValue(directory) ?? ".", host.WorkingDirectory),
            Environment = parse.GetValue(environment) ?? host.Variables.GetValueOrDefault(ConfigurationSources.EnvironmentVariable),
            EnvironmentVariables = host.Variables,
            RunOptions = runOptions,
        });
    }

    /// <summary>Prints every error; the exit code says whether there were any.</summary>
    public static int ReportErrors(LoadedConfiguration configuration, SofEnvironment host)
    {
        foreach (var error in configuration.Errors)
        {
            host.Error.WriteLine($"error: {error}");
        }

        if (configuration.IsValid)
        {
            return ExitCodes.Success;
        }

        host.Error.WriteLine(configuration.Errors.Count == 1 ? "1 error." : $"{configuration.Errors.Count} errors.");
        return ExitCodes.Invalid;
    }
}
