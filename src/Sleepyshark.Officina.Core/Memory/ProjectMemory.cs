using System.Globalization;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Memory;

/// <summary>
/// Project memory as one agent or the owner acts on it (MEM). An agent proposes a change; the lead or the owner, as
/// configured, approves it (MEM-03). Only an approved change is memory. Every proposal and decision is kept in the
/// log, so a replaced entry stays readable and nothing is dropped silently (MEM-05); a decision that replaces an
/// earlier one says so (MEM-02). Changes append to the log with the next revision, and when another change takes that
/// revision first, the change is checked again against the log as it is then, as the run record does (CONC-01).
/// </summary>
public sealed class ProjectMemory
{
    /// <summary>Who the owner's changes are attributed to. Only <c>owner: true</c> gives the owner's authority, so no agent's name can take it.</summary>
    public const string Owner = "owner";

    private readonly IMemoryStore store;
    private readonly string? tenant;
    private readonly string scope;
    private readonly ProjectMemoryOptions options;
    private readonly TimeProvider time;
    private readonly string by;
    private readonly bool owner;

    internal ProjectMemory(IMemoryStore store, string? tenant, string scope, ProjectMemoryOptions options, TimeProvider time, string by, bool owner = false)
    {
        this.store = store;
        this.tenant = tenant;
        this.scope = scope;
        this.options = options;
        this.time = time;
        this.by = by;
        this.owner = owner;
    }

    /// <summary>Which memory a caller's agents share: the project's, the caller's or the tenant's (MEM-04).</summary>
    internal static string ScopeOf(ProjectMemoryOptions options, ProjectOptions project, Caller caller) => options.Scope switch
    {
        MemoryScope.Project => $"project:{project.Name}",
        MemoryScope.Owner => OwnerScope(caller.Id),
        _ => "tenant",
    };

    /// <summary>Whether the owner, and not the lead, approves agents' changes (MEM-03).</summary>
    internal bool OwnerApproves => options.ApproveBy == MemoryApprover.Owner;

    /// <summary>The scope of the memory that belongs to an owner, which export and deletion cover (PRIV-02).</summary>
    public static string OwnerScope(string? owner) => $"owner:{owner}";

    /// <summary>The same memory, as the owner acts on it.</summary>
    internal ProjectMemory AsOwner() => new(store, tenant, scope, options, time, Owner, owner: true);

    /// <summary>Memory as it is now: its entries, the proposals waiting, and its revision.</summary>
    public async ValueTask<MemoryState> ReadAsync(CancellationToken ct) => new(await store.ReadAsync(tenant, scope, ct).ConfigureAwait(false));

    /// <summary>Proposes a change; it is memory once it is approved.</summary>
    /// <returns>The proposal's number, or null and why it was rejected.</returns>
    public async Task<(long? Id, string Text)> ProposeAsync(MemoryProposal proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        while (true)
        {
            var state = await ReadAsync(ct).ConfigureAwait(false);
            if (Problem(proposal, state) is { } problem)
            {
                return (null, problem);
            }

            var id = state.Proposals.Count + 1;
            if (await store.TryAppendAsync(tenant, Change(state, MemoryAction.Proposed, id, proposal, null), ct).ConfigureAwait(false))
            {
                return (id, $"Proposed as #{id}.");
            }
        }
    }

    /// <summary>Approves a proposal, which becomes memory (MEM-03), unless that would take memory past its size limit (MEM-05).</summary>
    public Task<(bool Accepted, string Text)> ApproveAsync(long id, string? comment, CancellationToken ct) => DecideAsync(id, MemoryAction.Approved, comment, ct);

    /// <summary>Rejects a proposal. It stays in the log.</summary>
    public Task<(bool Accepted, string Text)> RejectAsync(long id, string comment, CancellationToken ct) => DecideAsync(id, MemoryAction.Rejected, comment, ct);

    /// <summary>The tokens of a text, one for four characters: an estimate that needs no tokenizer.</summary>
    internal static int Tokens(string text) => (text.Length + 3) / 4;

    private async Task<(bool Accepted, string Text)> DecideAsync(long id, MemoryAction action, string? comment, CancellationToken ct)
    {
        while (true)
        {
            var state = await ReadAsync(ct).ConfigureAwait(false);
            if (state.Proposals.GetValueOrDefault(id) is not { Pending: true } proposal)
            {
                return (false, $"#{id} is not a proposal waiting for a decision.");
            }

            if (Authority(proposal) is { } refused)
            {
                return (false, refused);
            }

            if (action == MemoryAction.Approved)
            {
                if (Problem(proposal.Content, state) is { } problem)
                {
                    return (false, $"{problem} Propose it again.");
                }

                var current = state.Current();
                var next = Render([.. current.Where(entry => !proposal.Content.Replaced.Contains(entry.Id)), new MemoryEntry(id, proposal.Content, proposal.By, time.GetUtcNow())]);
                if (Tokens(next) > options.MaxTokens && Tokens(next) > Tokens(Render(current)))
                {
                    // MEM-05: nothing is dropped to make room, and the proposal stays for the owner.
                    return (false, $"memory would be {Tokens(next)} tokens, over its limit of {options.MaxTokens}. Propose a condensed version that replaces several entries with one; the owner reviews it.");
                }
            }

            if (await store.TryAppendAsync(tenant, Change(state, action, id, null, comment), ct).ConfigureAwait(false))
            {
                return (true, $"{action} #{id}.");
            }
        }
    }

    /// <summary>Why this approver may not decide on the proposal; null when they may (MEM-03, MEM-05).</summary>
    private string? Authority(Proposal proposal) =>
        owner ? null
        : options.ApproveBy != MemoryApprover.Lead ? "the owner decides on changes."
        : proposal.By == by ? "you cannot decide on your own proposal."
        : proposal.Content.Condenses ? "the owner reviews condensing."
        : null;

    /// <summary>Why the proposal cannot be made against memory as it is; null when it can (MEM-02).</summary>
    private static string? Problem(MemoryProposal proposal, MemoryState state)
    {
        if (string.IsNullOrWhiteSpace(proposal.Subject) || string.IsNullOrWhiteSpace(proposal.Text))
        {
            return "a change needs a subject and a text.";
        }

        if (proposal.Kind == MemoryKind.Decision && string.IsNullOrWhiteSpace(proposal.Reason))
        {
            return "a decision needs its reason.";
        }

        var current = state.Current().Select(entry => entry.Id).ToHashSet();
        return proposal.Replaced.FirstOrDefault(id => !current.Contains(id)) is var missing and > 0 ? $"#{missing} is not in memory, so it cannot be replaced." : null;
    }

    private MemoryChange Change(MemoryState state, MemoryAction action, long id, MemoryProposal? content, string? comment) =>
        new(scope, state.Log.Count == 0 ? 1 : state.Log[^1].Revision + 1, by, time.GetUtcNow(), action, id, content, comment);

    /// <summary>The text memory adds to the prefix: its entries in the order they were added, each with its number (MEM-01).</summary>
    internal static string Render(IReadOnlyList<MemoryEntry> entries) => entries.Count == 0
        ? ""
        : $"<project-memory>\n{Lines(entries)}\n</project-memory>";

    /// <summary>The entries, one line each; a decision has its reason, author and date (MEM-02), and a replacement says what it replaces.</summary>
    internal static string Lines(IEnumerable<MemoryEntry> entries) => string.Join('\n', entries.Select(entry =>
    {
        var content = entry.Content;
        var line = $"- {(content.Kind == MemoryKind.Decision ? "decision" : "note")} #{entry.Id} {content.Subject}: {content.Text}";
        line += content.Kind == MemoryKind.Decision
            ? $" Why: {content.Reason} (decided by {entry.Author} on {entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})"
            : "";
        return content.Replaced.Count > 0 ? $"{line} Replaces {string.Join(", ", content.Replaced.Select(id => $"#{id}"))}." : line;
    }));
}
