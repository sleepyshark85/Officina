using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// <c>sof init [--dir &lt;folder&gt;] [--build &lt;command&gt;] [--test &lt;command&gt;] [--force]</c>: writes the coding team's
/// <c>sof.json</c>, which needs only the project's build and test commands (CFG-17). It detects them where it can
/// (<see cref="CommandDetection"/>) and asks the owner to confirm or edit each at the console, unless an option gives it.
/// Then it validates the file as <c>sof config validate</c> does.
/// </summary>
internal static class InitCommand
{
    private const string Budget = "3";

    /// <summary>Writes a command as it is typed, quotes and <c>&amp;&amp;</c> included.</summary>
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Command Create(SofEnvironment host)
    {
        var directory = new Option<string>("--dir") { Description = "The folder to write sof.json in: the git repository's top folder (default: the current directory)." };
        var build = new Option<string>("--build") { Description = "The project's build command; with --test too, nothing is asked." };
        var test = new Option<string>("--test") { Description = "The project's test command." };
        var force = new Option<bool>("--force") { Description = "Replace an existing sof.json." };
        var command = new Command("init", "Write a sof.json for the coding team, asking only for the build and test commands, which it detects where it can.")
        {
            directory, build, test, force,
        };

        // Synchronous: a console read blocks its thread, so it is never awaited.
        command.SetAction(parse => Run(
            host, Path.GetFullPath(parse.GetValue(directory) ?? ".", host.WorkingDirectory), parse.GetValue(build), parse.GetValue(test), parse.GetValue(force)));
        return command;
    }

    private static int Run(SofEnvironment host, string directory, string? build, string? test, bool force)
    {
        if (!Directory.Exists(directory))
        {
            host.Error.WriteLine($"error: {directory} does not exist.");
            return ExitCodes.Usage;
        }

        var file = Path.Combine(directory, "sof.json");
        if (File.Exists(file) && !force)
        {
            host.Error.WriteLine($"error: {file} exists already, and sof init does not replace it. Give --force to replace it.");
            return ExitCodes.Usage;
        }

        if (GitWarning(directory) is { } warning)
        {
            host.Error.WriteLine($"warning: {warning}");
        }

        var found = CommandDetection.Detect(directory);
        if (found.Count == 0)
        {
            host.Out.WriteLine("Found no build or test commands to suggest.");
        }

        foreach (var (detection, index) in found.Select((detection, index) => (detection, index)))
        {
            var commands = string.Join(" and ", new[] { detection.Build, detection.Test }.OfType<string>());
            host.Out.WriteLine(index == 0
                ? $"Found {detection.Toolchain} ({detection.Why}){(commands.Length > 0 ? $": {commands}" : "")}."
                : $"Also found {detection.Toolchain} ({detection.Why}); sof init suggests the first.");
        }

        var asks = build is null || test is null;
        build ??= Ask(host, "Build command", found.Select(detection => detection.Build).FirstOrDefault());
        test ??= build is null ? null : Ask(host, "Test command", found.Select(detection => detection.Test).FirstOrDefault());
        if (build is null || test is null)
        {
            host.Error.WriteLine($"error: no {(build is null ? "build" : "test")} command, and the input ended. Give both with --build and --test.");
            return ExitCodes.Usage;
        }

        var toolchains = OperatingSystem.IsLinux() ? CommandDetection.Toolchains([build, test], host.Variables.GetValueOrDefault("PATH")) : [];
        var text = Configuration(build, test, toolchains);
        File.WriteAllText(file, text);
        host.Out.WriteLine($"Wrote {file}:");
        host.Out.WriteLine(text);
        if (toolchains.Count > 0)
        {
            host.Out.WriteLine($"note: the sandbox shows only the system folders, so toolchains lists where the commands' programs are: {string.Join(", ", toolchains)}.");
        }

        IgnoreState(host, directory, asks);

        var code = ValidateCommand.Validate(SofConfiguration.Load(directory, null, host.Variables, []), host);
        if (code == ExitCodes.Success)
        {
            host.Out.WriteLine("Next: commit, switch to a branch of its own, and run sof --agent team with ANTHROPIC_API_KEY set.");
        }

        return code;
    }

    /// <summary>The owner's answer, or <paramref name="suggested"/> on an empty line; null when the input ends with nothing to take.</summary>
    private static string? Ask(SofEnvironment host, string what, string? suggested)
    {
        while (true)
        {
            host.Out.Write(suggested is null ? $"{what}: " : $"{what} [{suggested}]: ");
            host.Out.Flush();
            var line = host.In.ReadLine()?.Trim();
            if (line is null)
            {
                host.Out.WriteLine();
                return suggested;
            }

            if (line.Length > 0 || suggested is not null)
            {
                return line.Length > 0 ? line : suggested;
            }
        }
    }

    /// <summary>The coding team's <c>sof.json</c>, with what the commands need in the sandbox.</summary>
    internal static string Configuration(string build, string test, IReadOnlyList<string> toolchains)
    {
        var sandbox = new JsonObject();
        if (CommandDetection.Hosts([build, test]) is { Count: > 0 } hosts)
        {
            sandbox["allowedHosts"] = new JsonArray([.. hosts.Select(host => JsonValue.Create(host))]);
        }

        if (toolchains.Count > 0)
        {
            sandbox["toolchains"] = new JsonArray([.. toolchains.Select(folder => JsonValue.Create(folder))]);
        }

        List<(string Match, string Action)> rules = [.. CommandDetection.Allowed([build, test]).Select(match => (match, "allow")), ("git push*", "deny"), ("git remote*", "deny")];
        sandbox["commandRules"] = new JsonArray([.. rules.Select(rule => new JsonObject { ["match"] = rule.Match, ["action"] = rule.Action })]);
        var configuration = new JsonObject
        {
            ["extends"] = new JsonArray("preset:coding-team"),
            ["project"] = new JsonObject { ["values"] = new JsonObject { ["buildCommand"] = build, ["testCommand"] = test } },
            ["run"] = new JsonObject { ["budget"] = new JsonObject { ["cost"] = JsonNode.Parse(Budget) } },
            ["capabilities"] = new JsonObject { ["sandbox"] = sandbox },
        };
        return $"""
            // Written by sof init: the coding team of preset:coding-team, with this project's build and test commands.
            // run.budget: what each message to the team, and each sof run, may spend before you are asked to go on.
            // commandRules replaces the preset's list whole, so it repeats the preset's git push and git remote denies.
            // sof config show --origin lists every setting and where it comes from.
            {Format(configuration, "")}

            """;
    }

    /// <summary>JSON for a person to read and edit: an object's settings one to a line, and each object in a list on a line of its own.</summary>
    private static string Format(JsonNode node, string indent) => node switch
    {
        JsonObject settings => $"{{\n{string.Join(",\n", settings.Select(setting => $"{indent}  \"{setting.Key}\": {Format(setting.Value!, $"{indent}  ")}"))}\n{indent}}}",
        JsonArray list when list.Any(item => item is JsonObject) =>
            $"[\n{string.Join(",\n", list.Select(item => $"{indent}  {Inline(item!)}"))}\n{indent}]",
        _ => Inline(node),
    };

    private static string Inline(JsonNode node) => node switch
    {
        JsonObject settings => $"{{ {string.Join(", ", settings.Select(setting => $"\"{setting.Key}\": {Inline(setting.Value!)}"))} }}",
        JsonArray list => $"[{string.Join(", ", list.Select(item => Inline(item!)))}]",
        _ => node.ToJsonString(Readable),
    };

    /// <summary>Why the folder is not where <c>sof</c> works, if it is not: the top folder of a git repository.</summary>
    private static string? GitWarning(string directory)
    {
        if (Path.Exists(Path.Combine(directory, ".git")))
        {
            return null;
        }

        for (var parent = Directory.GetParent(directory); parent is not null; parent = parent.Parent)
        {
            if (Path.Exists(Path.Combine(parent.FullName, ".git")))
            {
                return $"{directory} is a subfolder of the git repository {parent.FullName}, and sof needs sof.json in the repository's top folder.";
            }
        }

        return $"{directory} is not a git repository, and the coding team works in one: run git init and commit before you run sof.";
    }

    /// <summary>Adds <c>.sof/</c>, where runs are stored, to <c>.gitignore</c> if the owner agrees; with no prompts, says to.</summary>
    private static void IgnoreState(SofEnvironment host, string directory, bool ask)
    {
        var file = Path.Combine(directory, ".gitignore");
        var lines = File.Exists(file) ? File.ReadAllLines(file) : [];
        if (lines.Any(line => line.Trim() is ".sof" or ".sof/" or "/.sof" or "/.sof/"))
        {
            return;
        }

        if (!ask)
        {
            host.Out.WriteLine("note: add .sof/ to .gitignore: runs are stored there.");
            return;
        }

        host.Out.Write("Add .sof/, where runs are stored, to .gitignore? [Y/n]: ");
        host.Out.Flush();
        var answer = host.In.ReadLine()?.Trim();
        if (answer is null || answer.StartsWith('n') || answer.StartsWith('N'))
        {
            host.Out.WriteLine($"{(answer is null ? Environment.NewLine : "")}note: add .sof/ to .gitignore yourself: runs are stored there.");
            return;
        }

        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        File.AppendAllText(file, $"{(text.Length > 0 && !text.EndsWith('\n') ? "\n" : "")}.sof/\n");
        host.Out.WriteLine("Added .sof/ to .gitignore.");
    }
}
