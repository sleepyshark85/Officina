namespace Sleepyshark.Officina.Cli.Commands;

/// <summary>The options of a command, read from its arguments: <c>--name value</c>, <c>--name=value</c> and flags.</summary>
internal sealed class CommandArguments
{
    private readonly Dictionary<string, List<string>> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> flags = new(StringComparer.Ordinal);

    private CommandArguments()
    {
    }

    public static CommandArguments Parse(IEnumerable<string> args, IReadOnlySet<string> valueOptions, IReadOnlySet<string> flagOptions)
    {
        var parsed = new CommandArguments();
        using var reader = args.GetEnumerator();
        while (reader.MoveNext())
        {
            var arg = reader.Current;
            var (name, inline) = arg.IndexOf('=', StringComparison.Ordinal) is var equals and > 0 && arg.StartsWith("--", StringComparison.Ordinal)
                ? (arg[..equals], arg[(equals + 1)..])
                : (arg, null);

            if (flagOptions.Contains(name) && inline is null)
            {
                parsed.flags.Add(name);
            }
            else if (valueOptions.Contains(name))
            {
                var value = inline ?? (reader.MoveNext() ? reader.Current : throw new UsageException($"{name} needs a value."));
                if (!parsed.values.TryGetValue(name, out var list))
                {
                    parsed.values[name] = list = [];
                }

                list.Add(value);
            }
            else
            {
                throw new UsageException($"Unknown option \"{arg}\".");
            }
        }

        return parsed;
    }

    public bool Has(string flag) => flags.Contains(flag);

    public string? Value(string option) => values.TryGetValue(option, out var list) ? list[^1] : null;

    public IReadOnlyList<string> Values(string option) => values.TryGetValue(option, out var list) ? list : [];
}

/// <summary>The command line is wrong; the message says how.</summary>
internal sealed class UsageException(string message) : Exception(message);
