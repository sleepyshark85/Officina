using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The sandbox that commands run in (SBX).</summary>
public sealed record SandboxOptions
{
    [Setting("The hosts commands may reach, through a filtering proxy. `*.` matches any subdomain. Empty means no network.",
        Example = """["api.nuget.org", "*.nuget.org"]""")]
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    [Setting("Rules for commands, in order. Each command of a command line is decided by the first rule that matches it, and the strictest decision applies. A command no rule matches is asked about.",
        Example = """[{ "match": "dotnet build*", "action": "allow" }, { "match": "git push*", "action": "deny" }]""")]
    public IReadOnlyList<CommandRule> CommandRules { get; init; } = [];

    [Setting("The secrets each agent's commands receive as environment variables, by agent name. Other agents' commands never see them.",
        Example = """{ "developer": ["NUGET_TOKEN"] }""")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Secrets { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>A rule for the commands that match a pattern (SBX-02).</summary>
public sealed record CommandRule
{
    [Setting("The command it applies to, where `*` matches any text and `?` any one character.", Example = "\"dotnet test*\"")]
    [Required(ErrorMessage = Messages.Required)]
    public required string Match { get; init; }

    [Setting("What a match decides: `allow`, `ask` a human, or `deny`.", Example = "\"allow\"")]
    public CommandAction Action { get; init; } = CommandAction.Deny;
}

/// <summary>What a command rule decides, from the least to the most strict.</summary>
public enum CommandAction
{
    Allow,
    Ask,
    Deny,
}
