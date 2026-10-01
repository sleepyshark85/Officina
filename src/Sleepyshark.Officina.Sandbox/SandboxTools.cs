using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Sandbox;

/// <summary>
/// The tools that run commands in one agent's working copy, each in the sandbox (SBX-01): run a command, and start,
/// read and stop background processes (SBX-03). Give <see cref="Run"/> and <see cref="Start"/> the
/// <see cref="CommandRules"/> gate (SBX-02). The agent's commands receive the secrets its role may use, which the tool
/// pipeline then removes from their output (SBX-05). Disposing the tools stops the agent's background processes; the
/// host does it when the agent, its task or the run ends.
/// </summary>
public sealed class SandboxTools : IAsyncDisposable
{
    public const string Run = "sandbox.run";
    public const string Start = "sandbox.start_process";
    public const string Read = "sandbox.read_process_output";
    public const string Stop = "sandbox.stop_process";

    private const string CommandSchema = """
        { "type": "object", "properties": { "command": { "type": "string", "description": "The shell command line." } }, "required": ["command"], "additionalProperties": false }
        """;

    private const string IdSchema = """
        { "type": "object", "properties": { "id": { "type": "string", "description": "The id start_process returned." } }, "required": ["id"], "additionalProperties": false }
        """;

    private readonly ISandbox sandbox;
    private readonly SandboxOptions options;
    private readonly string directory;
    private readonly IReadOnlyList<string> secrets;
    private readonly ConcurrentDictionary<string, Background> background = new();
    private int started;

    /// <param name="sandbox">Where commands run.</param>
    /// <param name="options">The sandbox settings.</param>
    /// <param name="agent">The agent the tools are for, whose secrets its commands receive.</param>
    /// <param name="directory">The agent's working copy.</param>
    /// <exception cref="InvalidOperationException">The machine cannot isolate commands, so none may run (SBX-07).</exception>
    public SandboxTools(ISandbox sandbox, SandboxOptions options, string agent, string directory)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(options);
        if (sandbox.Probe() is { } problem)
        {
            throw new InvalidOperationException($"Commands cannot run, because this machine cannot sandbox them: {problem}");
        }

        this.sandbox = sandbox;
        this.options = options;
        this.directory = directory;
        secrets = options.Secrets.GetValueOrDefault(agent) ?? [];
        Tools = new Dictionary<string, ITool>
        {
            [Run] = new Tool("Runs a shell command in your working copy, and returns its output and exit code.", CommandSchema, ToolKind.Write, RunAsync),
            [Start] = new Tool("Starts a shell command in the background, such as a server, and returns its id.", CommandSchema, ToolKind.Write, StartAsync),
            [Read] = new Tool("Returns a background process's output since you last read it, and whether it still runs.", IdSchema, ToolKind.Read, ReadAsync),
            [Stop] = new Tool("Stops a background process and everything it started.", IdSchema, ToolKind.Write, StopAsync),
        };
    }

    /// <summary>The tools, by the id that <c>extension:&lt;id&gt;</c> sources name.</summary>
    public IReadOnlyDictionary<string, ITool> Tools { get; }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in background.Keys)
        {
            if (background.TryRemove(id, out var process))
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<ToolResult> RunAsync(ToolCall call, CancellationToken ct)
    {
        // Leaving early, at the time limit or on cancellation, stops the command.
        await using var process = await StartCommandAsync(call, ct).ConfigureAwait(false);
        var output = new StringBuilder();
        await foreach (var line in process.Output.ReadAllAsync(ct).ConfigureAwait(false))
        {
            output.Append(line).Append('\n');
        }

        return ToolResult.Success($"{output}[exit code {await process.ExitCode.WaitAsync(ct).ConfigureAwait(false)}]");
    }

    private async ValueTask<ToolResult> StartAsync(ToolCall call, CancellationToken ct)
    {
        var id = $"p{Interlocked.Increment(ref started)}";
        background[id] = new Background(await StartCommandAsync(call, ct).ConfigureAwait(false));
        return ToolResult.Success($"Started {id}.");
    }

    private ValueTask<ToolResult> ReadAsync(ToolCall call, CancellationToken ct) =>
        ValueTask.FromResult(background.TryGetValue(Id(call), out var process) ? ToolResult.Success(process.Read()) : Unknown(call));

    private async ValueTask<ToolResult> StopAsync(ToolCall call, CancellationToken ct)
    {
        if (!background.TryRemove(Id(call), out var process))
        {
            return Unknown(call);
        }

        await process.DisposeAsync().ConfigureAwait(false);
        return ToolResult.Success(process.Read());
    }

    private async Task<ISandboxProcess> StartCommandAsync(ToolCall call, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>();
        foreach (var name in secrets)
        {
            environment[name] = await call.Secrets.GetAsync(name, ct).ConfigureAwait(false);
        }

        var command = call.Arguments.GetProperty("command").GetString()!;
        return await sandbox.StartAsync(new SandboxCommand(command, directory, SandboxLimits.Default, options.AllowedHosts, environment), ct).ConfigureAwait(false);
    }

    private static string Id(ToolCall call) => call.Arguments.GetProperty("id").GetString()!;

    private static ToolResult Unknown(ToolCall call) => ToolResult.Failed(ToolErrorCategory.InvalidArguments, $"there is no background process {Id(call)}");

    /// <summary>A background process, and the output the agent has not read yet.</summary>
    private sealed class Background : IAsyncDisposable
    {
        private readonly ISandboxProcess process;
        private readonly StringBuilder unread = new();
        private readonly Task drained;

        public Background(ISandboxProcess process)
        {
            this.process = process;
            drained = DrainAsync();
        }

        /// <summary>The output since the last read, then whether the process still runs.</summary>
        public string Read()
        {
            lock (unread)
            {
                var output = unread.ToString();
                unread.Clear();
                return output + (drained.IsCompleted && process.ExitCode.IsCompleted ? $"[exited with code {process.ExitCode.Result}]" : "[running]");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await process.DisposeAsync().ConfigureAwait(false);
            await drained.ConfigureAwait(false);
        }

        private async Task DrainAsync()
        {
            await foreach (var line in process.Output.ReadAllAsync().ConfigureAwait(false))
            {
                lock (unread)
                {
                    unread.Append(line).Append('\n');
                }
            }
        }
    }

    private sealed class Tool(string description, string inputSchema, ToolKind kind, Func<ToolCall, CancellationToken, ValueTask<ToolResult>> invoke) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(description, JsonDocument.Parse(inputSchema).RootElement.Clone(), kind, ParallelSafe: kind == ToolKind.Read);

        public ValueTask<ToolResult> InvokeAsync(ToolCall toolCall, CancellationToken ct) => invoke(toolCall, ct);
    }
}
