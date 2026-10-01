using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Sandbox;

/// <summary>
/// The command rules, as the gate of the tools that run commands (SBX-02). Each command of a command line is decided by
/// the first rule that matches it, and one no rule matches is asked about. The strictest decision applies to the whole
/// line. Rules sort what an agent means to do; they cannot see what an allowed program runs in turn, such as a build's
/// own steps, so the sandbox, its mounts and the proxy are the guarantee.
/// </summary>
public sealed partial class CommandRules(SandboxOptions options) : IGate
{
    /// <summary>The id the host registers the gate under, for <c>extension:sandbox.commandRules</c>.</summary>
    public const string Id = "sandbox.commandRules";

    public ValueTask<GateDecision> EvaluateAsync(GateContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Joining output streams, as in 2>&1, is not a second command.
        var line = StreamJoins().Replace(context.Arguments.GetProperty("command").GetString() ?? "", ">");
        var commands = Separators().Split(line).Select(command => command.Trim()).Where(command => command.Length > 0).ToList();

        // A substitution runs a command no rule sees, so a line with one is never allowed by rule alone.
        var action = commands.Select(Decide).Append(commands.Count == 0 || Substitution().IsMatch(line) ? CommandAction.Ask : CommandAction.Allow).Max();
        return ValueTask.FromResult(action switch
        {
            CommandAction.Allow => GateDecision.Allow,
            CommandAction.Deny => GateDecision.Deny("a command rule denies this command"),
            _ => GateDecision.Ask("no command rule allows this command"),
        });
    }

    // CommandAction is ordered from the least to the most strict.
    private CommandAction Decide(string command) =>
        options.CommandRules.FirstOrDefault(rule => FileSystemName.MatchesSimpleExpression(rule.Match, command, ignoreCase: false))?.Action
        ?? CommandAction.Ask;

    [GeneratedRegex(@"[0-9]*>&[0-9]+|&>")]
    private static partial Regex StreamJoins();

    [GeneratedRegex(@"[;&|\n]")]
    private static partial Regex Separators();

    [GeneratedRegex(@"`|[$<>]\(")]
    private static partial Regex Substitution();
}
