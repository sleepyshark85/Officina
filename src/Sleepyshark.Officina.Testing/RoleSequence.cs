namespace Sleepyshark.Officina.Testing;

/// <summary>
/// The message order the Claude API accepts, to check requests and saved conversations: the user speaks first; roles
/// alternate, except that a user message may follow tool results (the API joins them); an operator message follows a
/// user message and is last or followed by the assistant; and tool calls are followed by one message with exactly one
/// result per call, in call order.
/// </summary>
public static class RoleSequence
{
    /// <summary>What is wrong with the order of <paramref name="messages"/>, or null when nothing is.</summary>
    public static string? Problem(IReadOnlyList<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        for (var index = 0; index < messages.Count; index++)
        {
            var (message, previous) = (messages[index], index > 0 ? messages[index - 1] : null);
            List<string> calls = previous is null ? [] : Calls(previous);
            var problem =
                previous is null && (message.Role != Role.User || IsResults(message)) ? "does not start with a user message"
                : IsResults(message) && !Results(message).SequenceEqual(calls) ? "has tool results that do not answer the calls before them, one each in call order"
                : calls.Count > 0 && !IsResults(message) ? "has tool calls not followed by their results"
                : previous is not null && message.Role == previous.Role && !(IsResults(previous) && !IsResults(message))
                    ? $"has two {message.Role} messages in a row"
                : message.Role == Role.Operator && (previous!.Role != Role.User || IsResults(previous)) ? "has an operator message that does not follow a user message"
                : message.Role == Role.Operator && index + 1 < messages.Count && messages[index + 1].Role != Role.Assistant
                    ? "has an operator message that is neither last nor followed by an assistant message"
                : index == messages.Count - 1 && Calls(message).Count > 0 ? "ends with tool calls that have no results"
                : null;
            if (problem is not null)
            {
                return $"The messages {problem} (message {index + 1} of {string.Join(", ", messages.Select(Describe))}).";
            }
        }

        return null;
    }

    private static List<string> Calls(Message message) =>
        message.Role == Role.Assistant ? [.. message.Blocks.Select(block => block.ToolCall?.Id).OfType<string>()] : [];

    private static IEnumerable<string> Results(Message message) => message.Blocks.Select(block => block.ToolResult?.CallId ?? "");

    private static bool IsResults(Message message) => message.Role == Role.User && message.Blocks.Any(block => block.ToolResult is not null);

    private static string Describe(Message message) => IsResults(message) ? "ToolResults" : message.Role.ToString();
}
