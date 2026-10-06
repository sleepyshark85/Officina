using System.ComponentModel;
using System.Text;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Claude;

namespace BookshopAssistant;

/// <summary>What the summarizer writes about a session.</summary>
public sealed record SessionSummary(
    [property: Description("A title of a few words, naming the customers, books or orders the session was about.")] string Title,
    [property: Description("One to three sentences on what the staff member asked and what came of it.")] string Summary,
    [property: Description("Each change made to the shop's data, such as a customer added, an order placed or cancelled, or a restock, with its ids. Empty when nothing changed.")]
    IReadOnlyList<string> Changes);

/// <summary>
/// The session summarizer: a stateless agent with typed output and no tools. It reads a session's transcript as one
/// user message: the staff's messages, the assistant's replies, and each tool call with its input and outcome, so it can
/// tell which changes were made. Its $0.05 budget limits output, not input: a long session's summary may cost more.
/// </summary>
public static class SessionSummarizer
{
    /// <summary>The most characters of one tool result the transcript keeps.</summary>
    private const int ResultLength = 1_000;

    /// <summary>The summarizer's model: a short, typed answer needs little effort.</summary>
    public static ClaudeModel Model(string? apiKey = null) =>
        new(apiKey) { Model = ClaudeModel.Opus55, Effort = ClaudeEffort.Low, MaxOutputTokens = 4_000 };

    public static Agent Create(IModel model, TimeProvider time) => new()
    {
        Name = "summarizer",
        Model = model,
        Instructions = Instructions,
        Output = OutputContract.For<SessionSummary>(),
        Time = time,
    };

    /// <summary>Each summary's run: it may spend at most five cents.</summary>
    public static readonly RunOptions Options = new() { Budget = new Budget { Cost = 0.05m } };

    /// <summary>The session's transcript, as the summarizer reads it.</summary>
    public static string Transcript(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var calls = new Dictionary<string, ToolCall>();
        var text = new StringBuilder();
        foreach (var message in conversation.Messages.Where(message => message.Role != Role.Operator))
        {
            foreach (var block in message.Blocks)
            {
                if (block.ToolCall is { } call)
                {
                    calls[call.Id] = call;
                }

                var line = block switch
                {
                    { ToolCall: { } requested } => $"Tool call {requested.Name} {requested.Input}",
                    { ToolResult: { } result } => $"Tool result of {(calls.TryGetValue(result.CallId, out var called) ? called.Name : "a call")}{(result.IsError ? " (error)" : "")}: {Cut(result.Content)}",
                    { Text: { Length: > 0 } said } => $"{(message.Role == Role.User ? "Staff" : "Assistant")}: {said}",
                    _ => null,
                };
                if (line is not null)
                {
                    text.AppendLine(line);
                }
            }
        }

        return text.ToString();
    }

    private static string Cut(string text) =>
        text.Length <= ResultLength ? text : text[..(char.IsHighSurrogate(text[ResultLength - 1]) ? ResultLength - 1 : ResultLength)] + " […]";

    private const string Instructions = """
        You summarize a session between a member of staff of a small bookshop and the Bookshop Assistant, a chatbot
        that looks up and changes the shop's catalogue, stock, customers and orders. The user message is the session's
        transcript: the staff member's messages, the assistant's replies, and each tool call with its outcome.

        Write a title, a summary and the list of changes made. Count as changes only write tool calls that succeeded
        (adding a customer, placing or cancelling an order, restocking); a declined or failed call changed nothing.
        Name customers, books and orders with their ids. Write in British English, plainly.
        """;
}
