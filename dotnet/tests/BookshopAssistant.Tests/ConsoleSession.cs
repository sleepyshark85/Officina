using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Claude;
using Sleepyshark.Officina.Testing;

namespace BookshopAssistant.Tests;

/// <summary>
/// Drives the real console against the real database; only the model (a <see cref="ScriptedModel"/>) and the human
/// (input lines, which also answer approval prompts) are scripted.
/// </summary>
internal static class ConsoleSession
{
    /// <inheritdoc cref="TestServices.Start"/>
    public static readonly DateTimeOffset Start = TestServices.Start;

    /// <inheritdoc cref="TestServices.Dashboard"/>
    public static readonly Uri Dashboard = TestServices.Dashboard;

    /// <summary>Opus 5.5's price, which the scripted model charges, as the console's budgets need one.</summary>
    public static readonly ModelPrice Price = ClaudePrices.Table["claude-opus-5-5"];

    /// <summary>A scripted model with <see cref="Price"/>, declaring the compaction and clearing the agent uses.</summary>
    public static ScriptedModel Model(string settings = "scripted") =>
        new() { Price = Price, Settings = settings, Capabilities = ModelCapabilities.Compaction | ModelCapabilities.ContextEditing };

    /// <summary>Runs a session on the application's container and returns its transcript.</summary>
    /// <param name="database">The database the tools use.</param>
    /// <param name="model">A scripted model, or Claude in the live smoke test.</param>
    /// <param name="script">The staff member's input: each string is a typed line; each <see cref="Func{Task}"/> runs before the next line.</param>
    /// <param name="time">The clock; by default a fake one stopped at <see cref="Start"/>.</param>
    /// <param name="cancelOn">When the transcript first contains this text, the reply is cancelled, as by Ctrl+C.</param>
    /// <param name="budgets">The console's budgets; by default its own.</param>
    /// <param name="summaries">The summarizer's scripted model; without one, sessions are not summarized.</param>
    /// <param name="exports">The export server's endpoint; none by default.</param>
    /// <param name="demo">Whether the agent runs in demo mode.</param>
    /// <param name="memory">The memory store; by default an empty in-memory one.</param>
    public static async Task<string> RunAsync(
        BookshopDatabase database, IModel model, IEnumerable<object> script, TimeProvider? time = null, string? cancelOn = null, Budgets? budgets = null,
        ScriptedModel? summaries = null, Uri? exports = null, bool demo = false, IMemoryStore? memory = null)
    {
        var output = new Transcript(cancelOn);
        var services = TestServices.Create(database, model, summaries, demo);
        if (exports is not null)
        {
            await services.AddExportsAsync(exports, TestContext.Current.CancellationToken);
        }

        services.AddSingleton(new Terminal(new ScriptedInput(script), output, EchoInput: true));
        if (time is not null)
        {
            services.AddSingleton(time);
        }

        if (budgets is not null)
        {
            services.AddSingleton(budgets);
        }

        if (memory is not null)
        {
            services.AddSingleton(memory);
        }

        await using var provider = services.Build();
        var console = provider.GetRequiredService<BookshopConsole>();
        output.Console = console;
        await console.RunAsync(
            provider.GetRequiredKeyedService<Agent>(BookshopServices.Chat), provider.GetKeyedService<Agent>(BookshopServices.Summarizer));
        return output.ToString();
    }

    /// <summary>The id of the session the console names last in <paramref name="transcript"/>.</summary>
    public static string SessionId(string transcript) =>
        System.Text.RegularExpressions.Regex.Matches(transcript, @"(?:^|\n)(?:New )?[Ss]ession ([0-9a-f]{12})\.").Last().Groups[1].Value;

    /// <summary>A reply that streams <paramref name="text"/>, then requests <paramref name="calls"/>.</summary>
    public static ModelEvent[] SayThenCall(string text, params ToolCall[] calls) =>
    [
        new TextDelta(text),
        new BlockReceived(ScriptedModel.TextBlock(text)),
        .. calls.Select(call => new BlockReceived(ScriptedModel.ToolCallBlock(call))),
        new ModelStopped(ModelStopReason.ToolUse),
    ];

    public static ToolCall Call(string id, string name, object input) => new(id, name, JsonSerializer.Serialize(input));

    /// <summary>The tool results the model received in its last request.</summary>
    public static IReadOnlyList<ToolResult> LastResults(ScriptedModel model) =>
        [.. model.Requests[^1].Messages[^1].Blocks.Select(block => block.ToolResult!)];

    /// <summary>Asserts that <paramref name="parts"/> appear in <paramref name="transcript"/> in this order.</summary>
    public static void InOrder(string transcript, params string[] parts)
    {
        var at = 0;
        foreach (var part in parts)
        {
            var found = transcript.IndexOf(part, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected, after position {at}: {part}\nTranscript:\n{transcript}");
            at = found + part.Length;
        }
    }

    private sealed class ScriptedInput(IEnumerable<object> script) : TextReader
    {
        private readonly Queue<object> items = new(script);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (items.TryDequeue(out var item))
            {
                switch (item)
                {
                    case string line:
                        return line;
                    case Func<Task> action:
                        await action();
                        break;
                    default:
                        throw new ArgumentException($"A script holds lines and actions, not {item}.");
                }
            }

            return null;
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }

    private sealed class Transcript(string? cancelOn) : StringWriter(System.Globalization.CultureInfo.InvariantCulture)
    {
        private bool cancelled;

        public BookshopConsole? Console { get; set; }

        public override Task WriteAsync(string? value)
        {
            Write(value);
            if (!cancelled && cancelOn is not null && ToString().Contains(cancelOn, StringComparison.Ordinal))
            {
                cancelled = Console!.CancelReply();
            }

            return Task.CompletedTask;
        }
    }
}
