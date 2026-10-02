using Sleepyshark.Officina.Core.Configuration;
using System.CommandLine;

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

    public SofConfiguration Load(ParseResult parse, SofEnvironment host)
    {
        var options = new List<(string, string, string)>();
        if (parse.GetValue(budget) is { } cost)
        {
            options.Add(("run.budget.cost", cost, "--budget"));
        }

        if (parse.GetValue(permissionMode) is { } mode)
        {
            options.Add(("run.permissionMode", mode, "--permission-mode"));
        }

        return SofConfiguration.Load(Directory(parse, host), Environment(parse, host), host.Variables, options);
    }

    /// <summary>The environment whose <c>sof.&lt;name&gt;.json</c> is merged, if any.</summary>
    public string? Environment(ParseResult parse, SofEnvironment host) => parse.GetValue(environment) ?? host.Variables.GetValueOrDefault("SOF_ENVIRONMENT");

    /// <summary>The directory with <c>sof.json</c>.</summary>
    public string Directory(ParseResult parse, SofEnvironment host) => Path.GetFullPath(parse.GetValue(directory) ?? ".", host.WorkingDirectory);

    /// <summary>The agent to run: the one named, or the only one; null, with the error printed, when there is none.</summary>
    public static string? Agent(string? named, OfficinaOptions options, SofEnvironment host)
    {
        var agents = options.Agents;
        if ((named ?? (agents.Count == 1 ? agents.Keys.Single() : null)) is { } name && agents.ContainsKey(name))
        {
            return name;
        }

        host.Error.WriteLine($"error: name the agent to run with --agent, one of: {string.Join(", ", agents.Keys.Order(StringComparer.Ordinal))}.");
        return null;
    }

    /// <summary>Prints every error; the exit code says whether there were any.</summary>
    public static int ReportErrors(SofConfiguration configuration, SofEnvironment host) => ReportErrors(configuration.Errors, host);

    /// <summary>Prints every error; the exit code says whether there were any.</summary>
    public static int ReportErrors(IReadOnlyList<ConfigurationError> errors, SofEnvironment host)
    {
        foreach (var error in errors)
        {
            host.Error.WriteLine($"error: {error}");
        }

        if (errors.Count == 0)
        {
            return ExitCodes.Success;
        }

        host.Error.WriteLine(errors.Count == 1 ? "1 error." : $"{errors.Count} errors.");
        return ExitCodes.Invalid;
    }
}
