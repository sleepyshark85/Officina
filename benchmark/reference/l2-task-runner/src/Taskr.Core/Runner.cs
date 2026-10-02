using System.Diagnostics;

namespace Taskr.Core;

/// <summary>
/// Runs planned tasks, each once its dependencies are done, at most <paramref name="parallel"/> at a time. After a failure no
/// new task starts; the running ones finish.
/// </summary>
public sealed class Runner(TaskFile file, State state, TextWriter output, int parallel)
{
    private readonly Lock writing = new();

    public async Task<int> RunAsync(IReadOnlyList<string> order)
    {
        var remaining = order.ToList();
        var done = new HashSet<string>();
        var running = new Dictionary<Task<int>, string>();
        var failed = false;
        while (remaining.Count > 0 || running.Count > 0)
        {
            if (!failed)
            {
                foreach (var name in remaining.Where(name => file.Tasks[name].Deps.All(done.Contains)).ToList())
                {
                    if (running.Count >= parallel)
                    {
                        break;
                    }

                    remaining.Remove(name);
                    running.Add(Task.Run(() => RunTaskAsync(name)), name);
                }
            }

            if (running.Count == 0)
            {
                break;
            }

            var finished = await Task.WhenAny(running.Keys);
            var finishedName = running[finished];
            running.Remove(finished);
            var code = await finished;
            if (code == 0)
            {
                done.Add(finishedName);
            }
            else
            {
                failed = true;
                Write($"{finishedName} failed with exit code {code}");
            }
        }

        return failed ? 1 : 0;
    }

    private async Task<int> RunTaskAsync(string name)
    {
        var task = file.Tasks[name];
        var cached = task.Inputs.Count > 0 && task.Outputs.Count > 0;
        var hash = cached ? file.Hash(name) : null;
        if (cached && file.OutputsExist(name) && state.Recorded(name) == hash)
        {
            Write($"{name}: up to date");
            return 0;
        }

        if (task.Command is null)
        {
            return 0;
        }

        var code = await RunCommandAsync(name, task);
        if (code == 0 && hash is not null)
        {
            state.Record(name, hash);
        }

        return code;
    }

    private async Task<int> RunCommandAsync(string name, TaskDefinition task)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{task.Command}\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", task.Command! } };
        start.WorkingDirectory = file.Folder;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        foreach (var (key, value) in task.Env)
        {
            start.Environment[key] = value;
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, line) => Print(name, line.Data);
        process.ErrorDataReceived += (_, line) => Print(name, line.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private void Print(string name, string? line)
    {
        if (line is not null)
        {
            Write($"[{name}] {line}");
        }
    }

    private void Write(string line)
    {
        lock (writing)
        {
            output.WriteLine(line);
            output.Flush();
        }
    }
}
