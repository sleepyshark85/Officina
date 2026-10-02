using System.Globalization;
using System.Text.Json;
using Taskr.Core;

const string Usage = "usage: taskr [-f <file>] [-j <n>] <task>... | --list | --dry-run <task>... | --clean";

var (path, parallel, mode) = ("taskr.json", Environment.ProcessorCount, "run");
var requested = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-f" when i + 1 < args.Length:
            path = args[++i];
            break;
        case "-j" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0:
            parallel = n;
            i++;
            break;
        case "--list" or "--dry-run" or "--clean":
            mode = args[i][2..];
            break;
        case var arg when arg.StartsWith('-'):
            Console.Error.WriteLine($"taskr: unknown option {arg}\n{Usage}");
            return 2;
        default:
            requested.Add(args[i]);
            break;
    }
}

TaskFile file;
try
{
    file = TaskFile.Load(path);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
{
    Console.Error.WriteLine($"taskr: cannot read {path}: {exception.Message}");
    return 2;
}

var state = new State(file.Folder);
switch (mode)
{
    case "list":
        foreach (var (name, task) in file.Tasks.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(task.Deps.Count == 0 ? name : $"{name}: {string.Join(", ", task.Deps)}");
        }

        return 0;
    case "clean":
        state.Clean();
        return 0;
}

if (requested.Count == 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

IReadOnlyList<string> order;
try
{
    order = file.Plan(requested);
}
catch (PlanException exception)
{
    foreach (var problem in exception.Problems)
    {
        Console.Error.WriteLine($"taskr: {problem}");
    }

    return 2;
}

if (mode == "dry-run")
{
    foreach (var name in order)
    {
        Console.WriteLine(name);
    }

    return 0;
}

return await new Runner(file, state, Console.Out, parallel).RunAsync(order);
