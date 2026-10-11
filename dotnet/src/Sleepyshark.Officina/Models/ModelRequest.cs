using System.Collections.Immutable;

namespace Sleepyshark.Officina;

/// <summary>One request: the prefix that stays the same for the conversation, then the conversation.</summary>
/// <param name="Prefix">Model settings, sorted tools, instructions, output schema and context management.</param>
/// <param name="Messages">The conversation, with the run's pending messages.</param>
/// <param name="MaxOutputTokens">
/// The most output tokens the remaining budget allows, if it limits them; the model uses the lower of this and its own.
/// Not part of the prefix.
/// </param>
public sealed record ModelRequest(RequestPrefix Prefix, ImmutableArray<Message> Messages, int? MaxOutputTokens = null);
