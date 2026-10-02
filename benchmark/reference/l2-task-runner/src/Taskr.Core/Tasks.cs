using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Taskr.Core;

/// <summary>A task of <c>taskr.json</c>.</summary>
public sealed record TaskDefinition(
    string? Command, IReadOnlyList<string> Deps, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs, IReadOnlyDictionary<string, string> Env);

/// <summary>A problem found before anything runs: an unknown task, a missing dependency or a cycle.</summary>
public sealed class PlanException(IReadOnlyList<string> problems) : Exception(string.Join(Environment.NewLine, problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

public sealed class TaskFile
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public required string Folder { get; init; }

    public required IReadOnlyDictionary<string, TaskDefinition> Tasks { get; init; }

    public static TaskFile Load(string path)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, RawTask>>(File.ReadAllText(path), Json)
            ?? throw new JsonException("The task file must hold an object of tasks.");
        return new()
        {
            Folder = Path.GetDirectoryName(Path.GetFullPath(path))!,
            Tasks = raw.ToDictionary(
                entry => entry.Key,
                entry => new TaskDefinition(entry.Value.Command, entry.Value.Deps ?? [], entry.Value.Inputs ?? [], entry.Value.Outputs ?? [], entry.Value.Env ?? [])),
        };
    }

    /// <summary>The tasks to run for the requested ones, each once, every task after its dependencies.</summary>
    public IReadOnlyList<string> Plan(IReadOnlyList<string> requested)
    {
        var problems = new List<string>();
        var order = new List<string>();
        var done = new HashSet<string>();
        var path = new List<string>();
        foreach (var name in requested)
        {
            if (!Tasks.ContainsKey(name))
            {
                problems.Add($"unknown task: {name}");
            }
            else
            {
                Visit(name);
            }
        }

        if (problems.Count > 0)
        {
            throw new PlanException(problems);
        }

        return order;

        void Visit(string name)
        {
            if (done.Contains(name))
            {
                return;
            }

            if (path.IndexOf(name) is var start and >= 0)
            {
                problems.Add("cycle: " + string.Join(" -> ", path.Skip(start).Append(name)));
                return;
            }

            path.Add(name);
            foreach (var dep in Tasks[name].Deps)
            {
                if (Tasks.ContainsKey(dep))
                {
                    Visit(dep);
                }
                else
                {
                    problems.Add($"{name} depends on {dep}, which does not exist");
                }
            }

            path.RemoveAt(path.Count - 1);
            done.Add(name);
            order.Add(name);
        }
    }

    /// <summary>
    /// The hash a task's last success is recorded with: its inputs' paths and contents, its command and its <c>env</c>.
    /// </summary>
    public string Hash(string name)
    {
        var task = Tasks[name];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Matches(task.Inputs).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(Folder, file).Replace('\\', '/') + "\0"));
            hash.AppendData(File.ReadAllBytes(file));
            hash.AppendData([0]);
        }

        hash.AppendData(Encoding.UTF8.GetBytes("command\0" + task.Command + "\0"));
        foreach (var (key, value) in task.Env.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"env\0{key}\0{value}\0"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Whether every output pattern names something that exists.</summary>
    public bool OutputsExist(string name) => Tasks[name].Outputs.All(pattern =>
        pattern.IndexOfAny(['*', '?']) >= 0 ? Matches([pattern]).Any() : Path.Exists(Path.Combine(Folder, pattern)));

    private IEnumerable<string> Matches(IReadOnlyList<string> patterns)
    {
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(patterns);
        return matcher.GetResultsInFullPath(Folder);
    }

    private sealed record RawTask(string? Command, List<string>? Deps, List<string>? Inputs, List<string>? Outputs, Dictionary<string, string>? Env);
}

/// <summary>The hash of each task's last success, kept in <c>.taskr/state.json</c> next to the task file.</summary>
public sealed class State(string folder)
{
    private readonly Lock gate = new();

    public string Directory => Path.Combine(folder, ".taskr");

    private string FilePath => Path.Combine(Directory, "state.json");

    public string? Recorded(string name)
    {
        lock (gate)
        {
            return Read().GetValueOrDefault(name);
        }
    }

    public void Record(string name, string hash)
    {
        lock (gate)
        {
            var state = Read();
            state[name] = hash;
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state));
        }
    }

    public void Clean()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private Dictionary<string, string> Read() =>
        File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? [] : [];
}
