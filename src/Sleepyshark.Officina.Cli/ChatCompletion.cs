using System.CommandLine;
using System.CommandLine.Parsing;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// What the chat session suggests for the word being typed: the <c>/</c> commands, a command's subcommands and options from the
/// command line's own tree, the agents' names, and the ids of the session's runs. It knows nothing of the terminal; the line
/// editor asks it (<see cref="TerminalReader"/>).
/// </summary>
/// <param name="root">The command line, whose commands and options are suggested.</param>
/// <param name="agents">The agents' names, and in a team the ids of its agents, as the run's commands take them.</param>
/// <param name="runs">The ids of the runs the session's messages started, the latest first.</param>
internal sealed class ChatCompletion(RootCommand root, Func<IEnumerable<string>> agents, Func<IEnumerable<string>> runs)
{
    /// <summary>The session's own commands, which are not in the command line.</summary>
    private static readonly string[] SessionCommands = ["status", "approve", "deny", "change", "answer", "tell", "mode", "pause", "cancel", "checkpoint", "board", "memory", "new", "help", "quit"];

    private static readonly string[] Modes = ["ask", "auto", "readOnly"];

    /// <summary>The completions of <paramref name="word"/>, after <paramref name="prefix"/>, which is what the line holds before it.</summary>
    public IReadOnlyList<string> Complete(string prefix, string word)
    {
        if (prefix.Trim().Length == 0)
        {
            // The first word: a command, unless it is a message.
            return word.StartsWith('/') ? Matching(Commands().Select(command => $"/{command}"), word) : [];
        }

        if (!prefix.StartsWith('/'))
        {
            return [];
        }

        var words = CommandLineParser.SplitCommandLine(prefix[1..]).ToList();
        return Matching(Candidates(words, word), word);
    }

    private IEnumerable<string> Candidates(List<string> words, string word)
    {
        switch (words)
        {
            case ["help", ..]:
                return words.Count == 1 ? root.Subcommands.Select(command => command.Name) : [];
            case ["tell" or "pause" or "cancel"]:
                return agents();
            case ["resume"]:
                return agents().Concat(runs()); // an agent the reply's run paused, or between replies a run
            case ["mode"]:
                return Modes;
            case [.., "--agent"]:
                return agents();
            case [.., "--permission-mode"]:
                return Modes;
        }

        // A command of the command line: walk its tree as far as the words go.
        Command command = root;
        foreach (var name in words.TakeWhile(name => !name.StartsWith('-')))
        {
            if (command.Subcommands.FirstOrDefault(sub => sub.Name == name) is not { } sub)
            {
                break;
            }

            command = sub;
        }

        if (command == root)
        {
            return [];
        }

        if (word.StartsWith('-'))
        {
            return command.Options.SelectMany(option => option.Aliases.Prepend(option.Name)).Where(name => name.StartsWith("--", StringComparison.Ordinal));
        }

        if (command.Subcommands.Count > 0)
        {
            return command.Subcommands.Select(sub => sub.Name);
        }

        // A run's id is the argument of report, resume and rollback.
        return command.Arguments.Any(argument => argument.Name == "run") && !words.Skip(1).Any(name => !name.StartsWith('-')) ? runs() : [];
    }

    /// <summary>Every command that can be typed after a <c>/</c>.</summary>
    private IEnumerable<string> Commands() =>
        SessionCommands.Concat(root.Subcommands.Select(command => command.Name)).Distinct().Order(StringComparer.Ordinal);

    private static List<string> Matching(IEnumerable<string> candidates, string word) =>
        [.. candidates.Distinct().Where(candidate => candidate.StartsWith(word, StringComparison.OrdinalIgnoreCase))];
}
