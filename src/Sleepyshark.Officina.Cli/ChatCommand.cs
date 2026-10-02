using System.CommandLine;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// <c>sof chat [--agent &lt;name&gt;] [--new]</c>: a session with an agent, or the coding team, that stays open (TRG-01's
/// conversation). Each line the owner types is a message, and each message is a run of its own, as <c>sof run</c>'s work is,
/// whose reply streams as the model writes it (LAT-02). The runs share the conversation the agent keeps with the owner in the
/// conversation store, so the context carries over from message to message and from session to session (CAP-05). Lines that
/// start with <c>/</c> are the console's commands, as in <c>sof run</c> (HITL, RUN-06, UX-01).
/// </summary>
internal static class ChatCommand
{
    private const string Store = "capabilities.conversationStore.enabled";

    /// <summary><c>sof chat</c>, which plain <c>sof</c> is too (<see cref="AddTo"/>).</summary>
    public static Command Create(ConfigurationCommandOptions shared, SofEnvironment host) =>
        AddTo(new Command("chat", "Chat with an agent: each line you type is a message, and the conversation carries over. Plain sof does the same."), shared, host);

    /// <summary>Makes <paramref name="command"/> start a chat session, with the chat's options and the shared ones.</summary>
    public static Command AddTo(Command command, ConfigurationCommandOptions shared, SofEnvironment host)
    {
        var agent = new Option<string>("--agent") { Description = "The agent to chat with, such as team for the coding team (default: the only agent, or the one you pick)." };
        var fresh = new Option<bool>("--new") { Description = "Start a new conversation instead of continuing the stored one." };
        command.Options.Add(agent);
        command.Options.Add(fresh);
        shared.AddTo(command);
        command.SetAction((parse, ct) =>
        {
            if (host.InSession)
            {
                host.Error.WriteLine("error: this is a chat session already; /new starts a new conversation.");
                return Task.FromResult(ExitCodes.Usage);
            }

            return new ChatSession(parse, shared, host, parse.GetValue(agent), parse.GetValue(fresh)).RunAsync(ct);
        });
        return command;
    }

    /// <summary>Why <c>sof chat</c> refuses to chat with the agent, if it does: <c>config validate</c> reports the same (CFG-06).</summary>
    internal static string? Refusal(SofConfiguration configuration, string agent) =>
        StoreRefusal(configuration) ?? TriggerRefusal(configuration.Options, agent);

    /// <summary>Every agent <c>sof chat</c> refuses, and why, for <c>config validate</c>.</summary>
    internal static IEnumerable<string> Refusals(SofConfiguration configuration) =>
        StoreRefusal(configuration) is { } store
            ? [store]
            : configuration.Options.Agents.Keys.Order(StringComparer.Ordinal).Select(agent => TriggerRefusal(configuration.Options, agent)).OfType<string>();

    /// <summary>
    /// The configuration a conversation's runs use: the conversation store on, and full history for the agent that keeps the
    /// conversation (<see cref="KeepsHistory"/>), unless the configuration sets its strategy. Only the conversation store set off
    /// is refused (<see cref="Refusal"/>), as chatting would override it.
    /// </summary>
    internal static OfficinaOptions Conversing(SofConfiguration configuration, string agent)
    {
        var options = configuration.Options;
        var keeps = KeepsHistory(options, agent);
        var agents = options.Agents;
        if (!configuration.Sets($"agents.{keeps}.context.history.strategy"))
        {
            var definition = agents[keeps];
            agents = new Dictionary<string, AgentDefinition>(agents, StringComparer.Ordinal)
            {
                [keeps] = definition with { Context = definition.Context with { History = definition.Context.History with { Strategy = HistoryStrategy.Full } } },
            };
        }

        return options with { Agents = agents, Capabilities = options.Capabilities with { ConversationStore = new() { Enabled = true } } };
    }

    /// <summary>The agent whose history the conversation is: a team's lead, as the team's other agents keep none, or the agent itself.</summary>
    internal static string KeepsHistory(OfficinaOptions options, string agent) =>
        options.Agents[agent].Pattern is { Type: PatternOptions.Team, Lead: { } lead } ? lead : agent;

    /// <summary>
    /// What a chat with an agent that works in the workspace outside a team loses: each message's run works in a fresh working copy
    /// of the branch, and what it changes and does not integrate goes when the reply ends. Only a team integrates its work. Whether a
    /// chat should keep one working copy for the session, or integrate at the end of each message, is the owner's open question.
    /// </summary>
    internal static string? WorkspaceWarning(OfficinaOptions options, string agent) =>
        options.Capabilities.Workspace.Enabled && options.Agents[agent].Pattern.Type != PatternOptions.Team
            ? $"agent \"{agent}\" works in the workspace, and each message works in a fresh working copy of the branch: what it changes " +
              "and does not integrate is lost when the reply ends, unless capabilities.workspace.keepWorkingCopies is on, which keeps " +
              "each message's copy, though the next message does not see it."
            : null;

    /// <summary>Every agent a chat with which loses its edits between messages, and why, for <c>config validate</c>.</summary>
    internal static IEnumerable<string> WorkspaceWarnings(OfficinaOptions options) =>
        options.Agents.Keys.Order(StringComparer.Ordinal).Select(agent => WorkspaceWarning(options, agent)).OfType<string>();

    private static string? StoreRefusal(SofConfiguration configuration) =>
        configuration.Sets(Store) && !configuration.Options.Capabilities.ConversationStore.Enabled
            ? $"sof chat keeps the conversation in the conversation store, which {Store} turns off. Remove that setting, or set it to true."
            : null;

    private static string? TriggerRefusal(OfficinaOptions options, string agent) =>
        options.Agents[agent].Triggers is { } triggers && !triggers.Contains(Trigger.Conversation)
            ? $"agent \"{agent}\" takes no conversations: agents.{agent}.triggers leaves out conversation. Add it, or chat with another agent."
            : null;
}

