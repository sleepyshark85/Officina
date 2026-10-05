using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sleepyshark.Officina;

/// <summary>
/// What an agent is (AGT-01): a model and instructions, and optionally tools (GEN-02). Immutable, so any number of runs
/// may share it at once (AGT-04).
/// </summary>
public sealed record AgentDefinition
{
    public required IModel Model
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The instructions, frozen for every conversation: nothing per user, run or date goes here (CTX-01).</summary>
    public required string Instructions
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = value;
        }
    }

    /// <summary>The tools, kept sorted by name so every request lists them in the same order (CTX-01).</summary>
    public ImmutableArray<Tool> Tools
    {
        get;
        init
        {
            var sorted = value.IsDefault ? [] : value.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            for (var index = 1; index < sorted.Length; index++)
            {
                if (sorted[index].Name == sorted[index - 1].Name)
                {
                    throw new ArgumentException($"Two tools are named '{sorted[index].Name}'.", nameof(value));
                }
            }

            field = sorted;
        }
    } = [];

    /// <summary>The typed output the agent requires, if any (OUT-01); without one, a run's result is its text (GEN-05).</summary>
    public OutputContract? Output { get; init; }

    /// <summary>Names the agent in the audit trail (AUD-03); it is not sent to the model.</summary>
    public string Name { get; init; } = "agent";

    /// <summary>Answers approval requests; without one, runs are unattended and calls that need approval are denied (GEN-04).</summary>
    public IApprover? Approver { get; init; }

    /// <summary>Where the audit trail goes; without one there is no trail, and nothing else changes (AUD-04).</summary>
    public IAuditSink? AuditSink { get; init; }

    /// <summary>
    /// Values that never reach the model, the events or the audit trail, such as a password a tool's back end uses: each is
    /// redacted from tool results, audit entries and telemetry (EVT-03, AUD-05).
    /// </summary>
    public ImmutableArray<string> Secrets
    {
        get;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>
    /// Whether telemetry carries message text, tool inputs and tool results (EVT-04); off by default, as the audit trail is
    /// for the record. <see cref="Secrets"/> are redacted from it either way (EVT-03).
    /// </summary>
    public bool TelemetryContent { get; init; }

    /// <summary>
    /// Limits on each run (BUD-01); none by default. It is not part of the prefix, so a host may give each run its own,
    /// such as what is left of a session's budget, with <c>agent with { Budget = … }</c>.
    /// </summary>
    public Budget? Budget { get; init; }

    /// <summary>The clock for audit times, durations and span times; tests give a fake one.</summary>
    public TimeProvider Time
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    } = TimeProvider.System;

    /// <summary>Runs the agent and returns its result; see <see cref="StreamAsync"/>.</summary>
    public async Task<RunResult> RunAsync(
        Conversation conversation, string message, string? context = null, CancellationToken cancellationToken = default)
    {
        RunResult? result = null;
        await foreach (var runEvent in StreamAsync(conversation, message, context, cancellationToken).ConfigureAwait(false))
        {
            result = (runEvent as RunEnded)?.Result ?? result;
        }

        return result!;
    }

    /// <summary>A stateless run (GEN-03): on a new conversation, which is discarded afterwards.</summary>
    public Task<RunResult> RunAsync(string message, string? context = null, CancellationToken cancellationToken = default) =>
        RunAsync(new Conversation(), message, context, cancellationToken);

    /// <summary>
    /// Runs the agent on <paramref name="conversation"/> (a new one, or one this definition's runs used before) with a new
    /// user <paramref name="message"/>, and streams what happens, ending with <see cref="RunEnded"/>. While the model asks
    /// for tools, the run runs them, appends their results as one message, and calls the model again.
    /// <paramref name="context"/>, if given, is appended after the message as an operator message (CTX-02). Cancelling ends
    /// the run as <see cref="StopReason.Cancelled"/> (AGT-05). The message and context are appended only together with the
    /// model's reply, so a run that gets none leaves the conversation unchanged. A host that stops reading the events
    /// abandons the run: the model call is disposed and no <see cref="RunEnded"/> comes. One run at a time may use a
    /// conversation; starting another throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public IAsyncEnumerable<RunEvent> StreamAsync(
        Conversation conversation, string message, string? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (context is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(context);
        }

        if (Budget?.Cost is not null && Model.Price is null)
        {
            throw new InvalidOperationException("The budget limits cost, but the model has no price.");
        }

        return RunEngine.StreamAsync(this, conversation, message, context, cancellationToken);
    }

    /// <summary>
    /// Whether this definition can run on <paramref name="conversation"/>: true for a new conversation or one started with
    /// the same tools, instructions and model settings; a run on any other fails with a prefix mismatch (CTX-04).
    /// </summary>
    public bool CanContinue(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return conversation.Fingerprint is null || conversation.Fingerprint == Fingerprint();
    }

    /// <summary>
    /// Replaces each of <see cref="Secrets"/> in <paramref name="text"/>, as written and as escaped inside a JSON string
    /// (a tool input is JSON text, where a <c>"</c> or <c>\</c> in a secret is escaped, and other characters may be
    /// escaped as <c>\uXXXX</c>).
    /// </summary>
    internal string Redact(string text) => Secrets
        .Where(secret => secret.Length > 0)
        .SelectMany(secret => new[]
        {
            secret, JsonEncodedText.Encode(secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value, JsonEncodedText.Encode(secret).Value,
        })
        .Aggregate(text, (redacted, secret) => redacted.Replace(secret, "[redacted]", StringComparison.Ordinal));

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="length"/> characters, never between the two halves of a
    /// surrogate pair.
    /// </summary>
    internal static string Cut(string text, int length) =>
        text.Length <= length ? text : text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];

    /// <summary>A hash of everything in the cached prefix: model settings, instructions, tools and output schema (CTX-04).</summary>
    internal string Fingerprint()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", Model.Settings);
            writer.WriteString("instructions", Instructions);
            writer.WriteStartArray("tools");
            foreach (var tool in Tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("inputSchema");
                writer.WriteRawValue(tool.InputSchema);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (Output is not null)
            {
                writer.WritePropertyName("output");
                writer.WriteRawValue(Output.Schema);
            }

            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}
