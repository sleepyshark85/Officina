using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Sleepyshark.Officina;

namespace Samples.Chat;

/// <summary>
/// A chat assistant: stateful, one conversation per user kept by the host as JSON, an optional tool, and memory per
/// user. The date is run context, never in the instructions. Text output, no approver: no tool needs approval.
/// </summary>
/// <param name="agent">The agent, as <see cref="Create"/> builds it.</param>
/// <param name="conversations">The host's storage, user id to conversation JSON; a database table in a real app.</param>
/// <param name="time">The clock the date comes from.</param>
public sealed class ChatAssistant(Agent agent, IDictionary<string, string> conversations, TimeProvider time)
{
    public static Agent Create(IModel model, IMemoryStore memory) => new()
    {
        Name = "chat",
        Model = model,
        Instructions = Instructions,
        Tools = [Temperature, MemoryTool.Create(memory)],
    };

    /// <summary>Answers <paramref name="message"/> from <paramref name="user"/>, whose id also names their memory's scope.</summary>
    public async Task<RunResult> ReplyAsync(string user, string message, CancellationToken cancellationToken = default)
    {
        var conversation = conversations.TryGetValue(user, out var saved) ? JsonSerializer.Deserialize<Conversation>(saved)! : new Conversation();

        // A conversation of an older agent, with other tools or instructions, cannot go on: start anew.
        if (!agent.CanContinue(conversation))
        {
            conversation = new Conversation();
        }

        var context = string.Create(CultureInfo.InvariantCulture, $"Today is {time.GetUtcNow():yyyy-MM-dd}.");
        var result = await agent.RunAsync(conversation, message, new() { Context = context, MemoryScope = user }, cancellationToken).ConfigureAwait(false);
        conversations[user] = JsonSerializer.Serialize(conversation);
        return result;
    }

    private static readonly Tool Temperature = Tool.FromFunction(
        "celsius_to_fahrenheit",
        "Converts a temperature from degrees Celsius to degrees Fahrenheit.",
        ToolKind.Read,
        ([Description("The temperature in degrees Celsius.")] double celsius) => (celsius * 9 / 5) + 32);

    private const string Instructions = """
        You are a friendly general assistant in a chat. Answer briefly and plainly. Use the conversion tool for
        temperatures rather than working them out. Your memory belongs to the user you are talking to: keep their
        preferences there, such as the units they like, check it when it may help, and follow it.
        """;
}
