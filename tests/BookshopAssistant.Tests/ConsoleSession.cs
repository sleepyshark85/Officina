using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Claude;
using Sleepyshark.Officina.Testing;

namespace BookshopAssistant.Tests;

/// <summary>
/// Drives the real console against the real database (TEST-09): only the model (a <see cref="ScriptedModel"/>) and the
/// human (scripted input lines, which also answer the approval prompts) are scripted.
/// </summary>
internal static class ConsoleSession
{
    /// <summary>Monday 5 October 2026, 08:00 UTC; the fake clock's local time zone is UTC.</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Where the console links runs to their traces.</summary>
    public static readonly Uri Dashboard = new("http://dashboard.test/");

    /// <summary>Opus 5.5's price, which the scripted model charges, as the console's budgets need a price.</summary>
    public static readonly ModelPrice Price = ClaudePrices.Table["claude-opus-5-5"];

    /// <summary>A scripted model with <see cref="Price"/>.</summary>
    public static ScriptedModel Model(string settings = "scripted") => new() { Price = Price, Settings = settings };

    /// <summary>Runs a session and returns its transcript.</summary>
    /// <param name="database">The database the tools use.</param>
    /// <param name="model">The scripted model.</param>
    /// <param name="script">
    /// The staff member's input: each string is a line they type, and each <see cref="Func{Task}"/> runs before the next line is read.
    /// </param>
    /// <param name="time">The console's clock; by default a fake one stopped at <see cref="Start"/>, which the agent always uses for its audit times.</param>
    /// <param name="cancelOn">When the transcript first contains this text, the console is asked to cancel the reply, as Ctrl+C does.</param>
    /// <param name="budgets">The console's budgets; by default, its own.</param>
    /// <param name="summaries">The summarizer's scripted model; without one, sessions are not summarized.</param>
    public static async Task<string> RunAsync(
        BookshopDatabase database, ScriptedModel model, IEnumerable<object> script, TimeProvider? time = null, string? cancelOn = null,
        Budgets? budgets = null, ScriptedModel? summaries = null)
    {
        var output = new Transcript(cancelOn);
        var audit = new AuditTable(database.DataSource);
        var clock = new FakeTimeProvider(Start);
        var console = new BookshopConsole(
            new ScriptedInput(script), output, time ?? clock, echoInput: true, audit,
            new SessionStore(database.DataSource), Dashboard, budgets: budgets);
        output.Console = console;
        await console.RunAsync(
            BookshopAgent.Create(model, database.Tools, console, audit, [BookshopDatabase.Password], clock),
            summaries is null ? null : SessionSummarizer.Create(summaries, clock));
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
