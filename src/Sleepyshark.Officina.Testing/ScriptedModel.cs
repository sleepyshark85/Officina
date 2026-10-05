using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// A model that answers with replies scripted in advance, in order, and records every request (TEST-01). It needs no
/// network or API key, and is safe to call from concurrent runs. Like the Claude API, it rejects a request whose
/// messages break the <see cref="RoleSequence"/>.
/// </summary>
public sealed class ScriptedModel : IModel
{
    private readonly Lock gate = new();
    private readonly Queue<IEnumerable<ModelEvent>> replies = new();
    private readonly List<ModelRequest> requests = [];

    public string Settings { get; init; } = "scripted";

    public string Provider { get; init; } = "scripted";

    public string Name { get; init; } = "scripted";

    /// <summary>What its tokens cost; none by default.</summary>
    public ModelPrice? Price { get; init; }

    /// <summary>What it declares it supports; none by default.</summary>
    public ModelCapabilities Capabilities { get; init; }

    /// <summary>The requests received so far, in order.</summary>
    public IReadOnlyList<ModelRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    /// <summary>Adds a reply that streams <paramref name="text"/> and ends.</summary>
    public ScriptedModel Reply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Reply(new TextDelta(text), new BlockReceived(TextBlock(text)), new ModelStopped(ModelStopReason.End));
    }

    /// <summary>Adds a reply that streams these events, in order.</summary>
    public ScriptedModel Reply(params ModelEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Enqueue(events);
    }

    /// <summary>Adds a call that streams <paramref name="streamedFirst"/> and then fails with <paramref name="exception"/>.</summary>
    public ScriptedModel Fail(Exception exception, params ModelEvent[] streamedFirst)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(streamedFirst);
        return Enqueue(ThenThrow(streamedFirst, exception));
    }

    /// <summary>Adds a reply that requests these tool calls and stops for tool use.</summary>
    public ScriptedModel CallTools(params ToolCall[] calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        return Reply([.. calls.Select(call => new BlockReceived(ToolCallBlock(call))), new ModelStopped(ModelStopReason.ToolUse)]);
    }

    /// <summary>A text block as a provider sends it, with its raw JSON.</summary>
    public static ContentBlock TextBlock(string text) =>
        new(text, JsonSerializer.Serialize(new { type = "text", text }));

    /// <summary>A tool call block as a provider sends it, with its raw JSON and its neutral view.</summary>
    public static ContentBlock ToolCallBlock(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var input = JsonDocument.Parse(call.Input);
        return new(null, JsonSerializer.Serialize(new { type = "tool_use", id = call.Id, name = call.Name, input = input.RootElement }), call);
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IEnumerable<ModelEvent>? reply;
        lock (gate)
        {
            requests.Add(request);
        }

        // An invalid request gets no reply, so the reply stays scripted for the next one, as the Claude API rejects it unanswered.
        CheckRoles(request);
        lock (gate)
        {
            replies.TryDequeue(out reply);
        }

        if (reply is null)
        {
            throw new InvalidOperationException($"The scripted model received request {Requests.Count} but has no reply left.");
        }

        foreach (var modelEvent in reply)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return modelEvent;
        }
    }

    private static void CheckRoles(ModelRequest request)
    {
        if (RoleSequence.Problem(request.Messages) is { } problem)
        {
            throw new InvalidOperationException($"The request is invalid: {problem}");
        }
    }

    private ScriptedModel Enqueue(IEnumerable<ModelEvent> reply)
    {
        lock (gate)
        {
            replies.Enqueue(reply);
        }

        return this;
    }

    private static IEnumerable<ModelEvent> ThenThrow(ModelEvent[] first, Exception exception)
    {
        foreach (var modelEvent in first)
        {
            yield return modelEvent;
        }

        throw exception;
    }
}
