using System.Collections.Immutable;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// Builds each model request of a conversation as DESIGN.md §3 describes. The stable prefix is built once, from the
/// definition alone, so it is the same for every caller, work item and turn (CTX-02, CTX-03). The history only grows
/// (CTX-10), except when it is shortened (HIST-01), and the volatile context, rebuilt for every call, is added at its end.
/// </summary>
internal sealed class Conversation
{
    /// <summary>Agents of one definition start throughout a run, often minutes apart, so the shared prefix is kept longer.</summary>
    private static readonly TimeSpan PrefixCacheLifetime = TimeSpan.FromHours(1);

    private readonly ModelRequest prefix;
    private readonly bool turnScoped;
    private List<Message> history = [];
    private int turnStart;
    private ModelRequest? previous;
    private IReadOnlyList<string> sent = [];

    /// <param name="profile">The model profile.</param>
    /// <param name="tools">The tools the agent is offered, sorted by name.</param>
    /// <param name="instructions">The agent's instructions, placeholders filled.</param>
    /// <param name="context">The agent's context settings.</param>
    /// <param name="capabilities">What the provider supports.</param>
    public Conversation(ModelProfile profile, ImmutableArray<ToolDefinition> tools, string instructions, ContextOptions context, ProviderCapabilities capabilities)
    {
        // CTX-11: longest lifetime first, and no more boundaries than the provider allows; the history's is kept first.
        CacheBoundary[] boundaries = [new(CachePoint.Instructions, PrefixCacheLifetime), new(CachePoint.History, context.HistoryCacheLifetime)];
        prefix = new ModelRequest(profile, tools, $"{instructions}\n\n{Labels.Policy}", [], [.. boundaries.TakeLast(capabilities.CacheBoundaries)]);
        turnScoped = capabilities.TurnScopedMessages;
    }

    public ImmutableArray<Message> History => [.. history];

    /// <summary>The messages of the current turn: those added since the earlier turns were continued.</summary>
    public ImmutableArray<Message> CurrentTurn => [.. history.Skip(turnStart)];

    /// <summary>Whether the history has been shortened.</summary>
    public bool Shortened { get; private set; }

    /// <summary>Continues the conversation from its earlier turns (CTX-06); the current turn starts after them.</summary>
    public void Continue(IEnumerable<Message> earlier)
    {
        history.AddRange(earlier);
        turnStart = history.Count;
    }

    public void Add(Message message) => history.Add(message);

    /// <summary>
    /// Replaces the history with a shortened one, the only change to history already sent (HIST-01), once it is checked
    /// (HIST-02). The next request is then checked against the shortened history.
    /// </summary>
    /// <returns>What is wrong with the shortened history, which is then not used; null when it replaced the history.</returns>
    public string? Shorten(ImmutableArray<Message> shortened)
    {
        var current = CurrentTurn;
        var content = shortened.IsDefault ? [] : shortened.SelectMany(message => message.Content).ToList();
        var results = content.OfType<ToolResultContent>().Select(result => result.ToolUseId).ToHashSet();
        var problem = shortened.IsDefaultOrEmpty || shortened[0].Role != Role.User ? "does not start with a user message"
            : !content.OfType<ToolUseContent>().All(request => results.Contains(request.Id)) ? "has a tool request without its result"
            : shortened.Length < current.Length || !shortened[^current.Length..].SequenceEqual(current) ? "changes the current turn"
            : null;
        if (problem is null)
        {
            history = [.. shortened];
            turnStart = shortened.Length - current.Length;
            previous = null;
            Shortened = true;
        }

        return problem;
    }

    /// <summary>
    /// The next call's request: the history with the volatile context added, except after a reply, so a paused
    /// reply is continued as it is. Before it is sent, it is checked to start with exactly what the previous call sent.
    /// </summary>
    /// <param name="facts">The volatile context, rebuilt for this call (LOOP-10); none when it is off.</param>
    /// <exception cref="InvalidOperationException">The request would change content already sent (CTX-10).</exception>
    public ModelRequest Next(IReadOnlyList<string> facts)
    {
        // A call made again after "input too long" has no reply, so its context may be the last message already.
        if (facts.Count > 0 && history[^1] is { Role: not Role.Assistant, TurnScoped: false })
        {
            AddVolatile(facts);
        }

        var request = prefix with { History = [.. history] };
        if (previous is not null && !request.StartsWith(previous))
        {
            throw new InvalidOperationException("The model request changes content an earlier request sent; history is append-only.");
        }

        previous = request;
        return request;
    }

    /// <summary>
    /// CTX-10: a fresh copy in a turn-scoped message, which the provider clears after the call. Otherwise it is kept in
    /// the history, so only the facts that changed since they were last sent are added.
    /// </summary>
    private void AddVolatile(IReadOnlyList<string> facts)
    {
        if (turnScoped)
        {
            history.Add(new Message(Role.System, [new TextContent(Labels.Context(facts))], turnScoped: true));
            return;
        }

        var changed = facts.Where((fact, index) => index >= sent.Count || sent[index] != fact).ToList();
        sent = facts;
        if (changed.Count > 0)
        {
            history.Add(Message.User(Labels.Context(changed)));
        }
    }
}
