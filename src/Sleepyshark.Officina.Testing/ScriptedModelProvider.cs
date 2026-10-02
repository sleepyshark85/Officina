using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// A model provider that answers with replies scripted in advance, in order, and shortens history as scripted. It needs
/// no network or API key. Agents that work at the same time, as a team's do, get replies of their own: a script made with
/// <see cref="When"/> answers only the requests it matches, in its own order, whatever order the agents call in.
/// </summary>
public sealed class ScriptedModelProvider : IModelProvider, IHistoryShortener
{
    private readonly Lock gate = new();
    private readonly ModelScript replies;
    private readonly List<(Func<ModelRequest, bool> Matches, ModelScript Script)> scripts = [];
    private readonly Queue<Func<ImmutableArray<Message>, IEnumerable<Message>>> shortenings = new();
    private readonly List<ModelRequest> requests = [];
    private int toolCalls;

    public ScriptedModelProvider() => replies = new(gate, NextCallId);

    public ProviderCapabilities Capabilities { get; init; } = ProviderCapabilities.None;

    public ProviderCapabilities CapabilitiesOf(string model) => Capabilities;

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

    /// <summary>The text of a request's work: its first message, which is what the agent was asked to do.</summary>
    public static string WorkOf(ModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.Concat(request.History[0].Content.OfType<TextContent>().Select(text => text.Text));
    }

    /// <summary>
    /// A script of its own for the requests that match, such as those of one agent: they are answered from it, in order, and the
    /// other requests from the main script. The first script that matches a request and has a reply left answers it.
    /// </summary>
    public ModelScript When(Func<ModelRequest, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var script = new ModelScript(gate, NextCallId);
        lock (gate)
        {
            scripts.Add((matches, script));
        }

        return script;
    }

    /// <summary>Adds a reply with this text, after which the model has finished.</summary>
    public ScriptedModelProvider Reply(string text)
    {
        replies.Reply(text);
        return this;
    }

    /// <summary>Adds a reply that streams these events, in order.</summary>
    public ScriptedModelProvider Reply(params ModelEvent[] events)
    {
        replies.Reply(events);
        return this;
    }

    /// <summary>
    /// Adds a call that fails with this failure (MDL-05), after streaming these events if there are any, as an error that
    /// arrives mid-stream does.
    /// </summary>
    public ScriptedModelProvider Fail(ModelFailure failure, TimeSpan? retryAfter = null, params ModelEvent[] streamedFirst)
    {
        replies.Fail(failure, retryAfter, streamedFirst);
        return this;
    }

    /// <summary>Adds a reply that asks for these tool calls, each with its arguments as JSON.</summary>
    public ScriptedModelProvider CallTools(params (string Tool, string Arguments)[] calls)
    {
        replies.CallTools(calls);
        return this;
    }

    /// <summary>Adds a shortening: what the provider's own mechanism makes of the history it is given.</summary>
    public ScriptedModelProvider Shorten(Func<ImmutableArray<Message>, IEnumerable<Message>> shorten)
    {
        ArgumentNullException.ThrowIfNull(shorten);
        lock (gate)
        {
            shortenings.Enqueue(shorten);
        }

        return this;
    }

    public ValueTask<ImmutableArray<Message>> ShortenAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            return shortenings.TryDequeue(out var shorten)
                ? ValueTask.FromResult<ImmutableArray<Message>>([.. shorten(request.History)])
                : throw new InvalidOperationException("The scripted model was asked to shorten history but has no shortening left. Add one with Shorten().");
        }
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModelEvent[] reply;
        lock (gate)
        {
            requests.Add(request);
            var next = scripts.Where(script => script.Matches(request)).Select(script => script.Script.Next()).FirstOrDefault(found => found is not null) ?? replies.Next();
            reply = next ?? throw new InvalidOperationException(
                $"The scripted model received request {requests.Count} but has no reply left. Add one with Reply().");
        }

        foreach (var modelEvent in reply)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (modelEvent is Failing failing)
            {
                throw failing.Exception;
            }

            yield return modelEvent;
        }
    }

    /// <summary>The id of the next tool call a script asks for, unique in the provider.</summary>
    private string NextCallId() => $"call-{Interlocked.Increment(ref toolCalls)}";

    internal sealed record Failing(ModelCallException Exception) : ModelEvent;
}

/// <summary>Replies the scripted model gives, in order.</summary>
public sealed class ModelScript
{
    private readonly Lock gate;
    private readonly Func<string> nextCallId;
    private readonly Queue<ModelEvent[]> replies = new();

    internal ModelScript(Lock gate, Func<string> nextCallId)
    {
        this.gate = gate;
        this.nextCallId = nextCallId;
    }

    /// <summary>Adds a reply with this text, after which the model has finished.</summary>
    public ModelScript Reply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Reply(new TextDelta(text), new Stopped(StopReason.Finished));
    }

    /// <summary>Adds a reply that streams these events, in order.</summary>
    public ModelScript Reply(params ModelEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (gate)
        {
            replies.Enqueue(events);
        }

        return this;
    }

    /// <summary>Adds a call that fails with this failure, after streaming these events if there are any.</summary>
    public ModelScript Fail(ModelFailure failure, TimeSpan? retryAfter = null, params ModelEvent[] streamedFirst)
    {
        ArgumentNullException.ThrowIfNull(streamedFirst);
        return Reply([.. streamedFirst, new ScriptedModelProvider.Failing(new ModelCallException(failure, retryAfter: retryAfter))]);
    }

    /// <summary>Adds a reply that asks for these tool calls, each with its arguments as JSON.</summary>
    public ModelScript CallTools(params (string Tool, string Arguments)[] calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        return Reply([.. calls.Select(call => new ContentReceived(
            new ToolUseContent(nextCallId(), call.Tool, JsonDocument.Parse(call.Arguments).RootElement))),
            new Stopped(StopReason.WantsTools)]);
    }

    /// <summary>The next reply, taken from the script; null when none is left. Called under the provider's lock.</summary>
    internal ModelEvent[]? Next() => replies.TryDequeue(out var next) ? next : null;
}
