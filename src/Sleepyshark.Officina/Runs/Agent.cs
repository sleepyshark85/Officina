using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sleepyshark.Officina;

/// <summary>What an agent is: a model and instructions, and optionally tools. Immutable, so runs may share it at once.</summary>
public sealed record Agent
{
    public required IModel Model
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Frozen for every conversation: nothing per user, run or date goes here.</summary>
    public required string Instructions
    {
        get;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = value;
        }
    }

    /// <summary>Kept sorted by name, so every request lists them in the same order.</summary>
    public ImmutableArray<Tool> Tools
    {
        get;
        init => field = RequestPrefix.Sorted(value);
    } = [];

    /// <summary>The typed output the agent requires, if any; without one, a run's result is its text.</summary>
    public OutputContract? Output { get; init; }

    /// <summary>Names the agent in the audit trail; not sent to the model.</summary>
    public string Name { get; init; } = "agent";

    /// <summary>Answers approval requests; without one, runs are unattended and calls needing approval are denied.</summary>
    public IApprover? Approver { get; init; }

    /// <summary>Where the audit trail goes; without one there is no trail, and nothing else changes.</summary>
    public IAuditSink? AuditSink { get; init; }

    /// <summary>
    /// Values that must never reach the model, events or audit trail, such as a tool's database password. Redacted from
    /// tool results, audit entries and telemetry.
    /// </summary>
    public ImmutableArray<string> Secrets
    {
        get;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>
    /// Whether telemetry carries message text, tool inputs and results; off by default, as the audit trail is the record.
    /// <see cref="Secrets"/> are redacted either way.
    /// </summary>
    public bool TelemetryContent { get; init; }

    /// <summary>How the provider shortens a long conversation; none by default. Needs the model's support; part of the prefix.</summary>
    public ContextManagement? ContextManagement { get; init; }

    /// <summary>The clock for audit, duration and span times; tests give a fake one.</summary>
    public TimeProvider Time
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    } = TimeProvider.System;

    /// <summary>Runs the agent and returns its result; see <see cref="StreamAsync"/>.</summary>
    public async Task<RunResult> RunAsync(
        Conversation conversation, string message, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        RunResult? result = null;
        await foreach (var runEvent in StreamAsync(conversation, message, options, cancellationToken).ConfigureAwait(false))
        {
            result = (runEvent as RunEnded)?.Result ?? result;
        }

        return result!;
    }

    /// <summary>A stateless run, on a new conversation that is discarded afterwards.</summary>
    public Task<RunResult> RunAsync(string message, RunOptions? options = null, CancellationToken cancellationToken = default) =>
        RunAsync(new Conversation(), message, options, cancellationToken);

    /// <summary>
    /// Runs the agent on <paramref name="conversation"/> with a user <paramref name="message"/> and streams what happens,
    /// ending with <see cref="RunEnded"/>. While the model asks for tools, the run runs them, appends their results as one
    /// message and calls the model again. The message and context are appended only with the model's reply, so a run that
    /// gets none leaves the conversation unchanged. Cancelling ends the run as <see cref="StopReason.Cancelled"/>; a host
    /// that stops reading the events abandons it, with no <see cref="RunEnded"/>. One run at a time may use a
    /// conversation; starting another throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public IAsyncEnumerable<RunEvent> StreamAsync(
        Conversation conversation, string message, RunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        options ??= new RunOptions();
        if (options.Budget?.Cost is not null && Model.Price is null)
        {
            throw new InvalidOperationException("The budget limits cost, but the model has no price.");
        }

        if ((ContextManagement?.CompactAt is not null && !Model.Capabilities.HasFlag(ModelCapabilities.Compaction))
            || (ContextManagement?.ClearToolResults is not null && !Model.Capabilities.HasFlag(ModelCapabilities.ContextEditing)))
        {
            throw new InvalidOperationException("The agent's context management needs a capability its model does not have.");
        }

        if (options.MemoryScope is null && Tools.Any(tool => tool.IsMemory))
        {
            throw new ArgumentException("The agent has memory, so the run needs a memory scope.", nameof(options));
        }

        return RunEngine.StreamAsync(this, conversation, message, options, cancellationToken);
    }

    /// <summary>
    /// Whether this agent can run on <paramref name="conversation"/>: true for a new one or one started with the same
    /// <see cref="RequestPrefix"/>; on any other a run fails with a prefix mismatch.
    /// </summary>
    public bool CanContinue(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return conversation.Fingerprint is null || conversation.Fingerprint == Prefix().Fingerprint;
    }

    /// <summary>
    /// Replaces each of <see cref="Secrets"/> in <paramref name="text"/>, as written and as escaped in a JSON string
    /// (tool inputs are JSON, where a secret's <c>"</c> or <c>\</c> is escaped, and other characters may be <c>\uXXXX</c>).
    /// Every occurrence of every form is found in the original text first and overlapping ones are merged, so secrets
    /// that overlap or contain each other are redacted whole, whatever their order.
    /// </summary>
    internal string Redact(string text)
    {
        var covered = new bool[text.Length];
        foreach (var form in Secrets.Where(secret => secret.Length > 0).SelectMany(secret => new[]
        {
            secret, JsonEncodedText.Encode(secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value, JsonEncodedText.Encode(secret).Value,
        }).Distinct(StringComparer.Ordinal))
        {
            for (var at = text.IndexOf(form, StringComparison.Ordinal); at >= 0; at = text.IndexOf(form, at + 1, StringComparison.Ordinal))
            {
                Array.Fill(covered, true, at, form.Length);
            }
        }

        var redacted = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            _ = !covered[i] ? redacted.Append(text[i]) : i == 0 || !covered[i - 1] ? redacted.Append("[redacted]") : redacted;
        }

        return redacted.ToString();
    }

    /// <summary>The part of every request that stays the same for a conversation, and identifies it.</summary>
    internal RequestPrefix Prefix() => new(Model.Settings, Tools, Instructions, Output?.Schema, ContextManagement);
}
