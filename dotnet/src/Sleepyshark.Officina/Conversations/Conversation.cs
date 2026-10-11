using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina;

/// <summary>
/// The append-only messages between an agent and its model. The host owns it and stores it as JSON between runs; the
/// core only appends. One run at a time may use it.
/// </summary>
public sealed class Conversation
{
    private int running;

    /// <summary>Identifies the conversation in the audit trail: random by default, or the host's own, such as a session id.</summary>
    [JsonInclude]
    [JsonPropertyName("id")]
    [JsonPropertyOrder(-1)]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The prefix fingerprint of the agent it was started with; null before its first run. A run of an agent with another
    /// fingerprint fails without calling the model.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; private set; }

    /// <summary>The messages, oldest first. Each read is a snapshot that later appends do not change.</summary>
    [JsonInclude]
    [JsonPropertyName("messages")]
    public ImmutableArray<Message> Messages { get; private set; } = [];

    internal void StartRun()
    {
        if (Interlocked.Exchange(ref running, 1) == 1)
        {
            throw new InvalidOperationException("Another run is using this conversation; one run at a time may use it.");
        }
    }

    internal void EndRun() => Volatile.Write(ref running, 0);

    internal void Bind(string fingerprint) => Fingerprint = fingerprint;

    internal void Append(Message message) => Messages = Messages.Add(message);
}
