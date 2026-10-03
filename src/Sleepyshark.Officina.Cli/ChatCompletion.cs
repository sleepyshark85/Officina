using System.CommandLine;
using System.CommandLine.Parsing;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// What the chat session suggests for the word being typed: the <c>/</c> commands, a command's subcommands and options from the
/// command line's own tree and <c>/task</c>'s, the agents' names, the ids of the session's runs, and the ids of the board's tasks. It knows nothing of the terminal; the line
/// editor asks it (<see cref="TerminalReader"/>).
/// </summary>
/// <param name="root">The command line, whose commands and options are suggested.</param>
/// <param name="agents">The agents' names, and in a team the ids of its agents, as the run's commands take them.</param>
/// <param name="runs">The ids of the runs the session's messages started, the latest first.</param>
/// <param name="tasks">The ids of the tasks on the board of the reply that runs, or of the last message's run.</param>
internal sealed class ChatCompletion(RootCommand root, Func<IEnumerable<string>> agents, Func<IEnumerable<string>> runs, Func<IEnumerable<string>> tasks)
{
    private readonly Command task = new TaskCommand().Command;

    /// <summary>The session's own commands, which are not in the command line.</summary>
    private static readonly string[] SessionCommands = ["status", "approve", "deny", "change", "answer", "tell", "mode", "pause", "cancel", "checkpoint", "board", "task", "memory", "new", "help", "quit"];

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
                return words.Count == 1 ? root.Subcommands.Append(task).Select(command => command.Name) : [];
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
            case ["task", ..] and [.., "--depends"]:
                return tasks();
        }

        // A command of the command line, or /task: walk its tree as far as the words go.
        Command? command = null;
        var walked = 0;
        foreach (var name in words.TakeWhile(name => !name.StartsWith('-')))
        {
            if ((command?.Subcommands ?? root.Subcommands.Append(task)).FirstOrDefault(sub => sub.Name == name) is not { } sub)
            {
                break;
            }

            command = sub;
            walked++;
        }

        if (command is null)
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

        // A task's id is the first argument of a /task command, and an agent the second of /task assign.
        var arguments = words.Skip(walked).Count(name => !name.StartsWith('-'));
        if (command.Parents.Contains(task))
        {
            return (arguments, command.Name) switch
            {
                (0, not "add") => tasks(),
                (1, "assign") => agents(),
                _ => [],
            };
        }

        // A run's id is the argument of report, resume and rollback.
        return command.Arguments.Any(argument => argument.Name == "run") && !words.Skip(1).Any(name => !name.StartsWith('-')) ? runs() : [];
    }

    /// <summary>
    /// Every command that can be typed after a <c>/</c>: <c>chat</c> is not one, as the session is a chat already, nor <c>init</c>,
    /// which runs outside a session.
    /// </summary>
    private IEnumerable<string> Commands() =>
        SessionCommands.Concat(root.Subcommands.Select(command => command.Name)).Where(command => command is not ("chat" or "init")).Distinct().Order(StringComparer.Ordinal);

    private static List<string> Matching(IEnumerable<string> candidates, string word) =>
        [.. candidates.Distinct().Where(candidate => candidate.StartsWith(word, StringComparison.Ordinal))];
}
