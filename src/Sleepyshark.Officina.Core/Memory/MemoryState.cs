namespace Sleepyshark.Officina.Core.Memory;

public enum MemoryKind
{
    /// <summary>Durable knowledge: an instruction, a convention, how to build and test, a summary of the architecture (MEM-01).</summary>
    Note,

    /// <summary>A choice that affects the whole project, with its reason (MEM-02).</summary>
    Decision,
}

public enum MemoryAction
{
    Proposed,
    Approved,
    Rejected,
}

/// <summary>A change to memory, as an agent or the owner proposes it.</summary>
/// <param name="Kind">A note or a decision.</param>
/// <param name="Subject">What it is about, such as <c>build</c>.</param>
/// <param name="Text">The note, or the choice made.</param>
/// <param name="Reason">Why; every decision has one.</param>
/// <param name="Replaces">The numbers of the entries it replaces, which then leave memory but stay in the log. Replacing several is condensing.</param>
public sealed record MemoryProposal(MemoryKind Kind, string Subject, string Text, string? Reason = null, IReadOnlyList<long>? Replaces = null)
{
    internal IReadOnlyList<long> Replaced => Replaces ?? [];

    /// <summary>Whether it replaces several entries with one, which the owner reviews (MEM-05).</summary>
    internal bool Condenses => Replaced.Count > 1;
}

/// <summary>One line of the log (MEM-03): a proposal, or the decision on one.</summary>
/// <param name="Scope">Whose memory.</param>
/// <param name="Revision">The place in the scope's log, from 1.</param>
/// <param name="By">The agent or the owner.</param>
/// <param name="Time">When.</param>
/// <param name="Action">Proposed, or approved or rejected.</param>
/// <param name="Proposal">The proposal's number: new for a proposal, otherwise the one decided on.</param>
/// <param name="Content">For a proposal, what it proposes.</param>
/// <param name="Comment">For a decision, why.</param>
public sealed record MemoryChange(
    string Scope, long Revision, string By, DateTimeOffset Time, MemoryAction Action, long Proposal, MemoryProposal? Content, string? Comment);

/// <summary>An approved change: a line of memory (MEM-01, MEM-02).</summary>
/// <param name="Id">The proposal's number, which later changes refer to.</param>
/// <param name="Content">What was approved.</param>
/// <param name="Author">Who proposed it.</param>
/// <param name="Date">When it was approved.</param>
/// <param name="ApprovedBy">Who approved it.</param>
/// <param name="Revision">The log revision that approved it.</param>
public sealed record MemoryEntry(long Id, MemoryProposal Content, string Author, DateTimeOffset Date, string ApprovedBy, long Revision = 0);

/// <summary>A proposal and where it stands.</summary>
/// <param name="Id">Its number.</param>
/// <param name="Content">What it proposes.</param>
/// <param name="By">Who proposed it.</param>
/// <param name="Time">When.</param>
/// <param name="Status">Proposed while it waits for a decision.</param>
public sealed record Proposal(long Id, MemoryProposal Content, string By, DateTimeOffset Time, MemoryAction Status)
{
    public bool Pending => Status == MemoryAction.Proposed;
}

/// <summary>Memory as its log makes it.</summary>
public sealed class MemoryState
{
    private readonly List<MemoryEntry> approved = [];
    private readonly Dictionary<long, Proposal> proposals = [];

    internal MemoryState(IReadOnlyList<MemoryChange> log)
    {
        Log = log;
        foreach (var change in log)
        {
            if (change.Action == MemoryAction.Proposed)
            {
                proposals[change.Proposal] = new(change.Proposal, change.Content!, change.By, change.Time, MemoryAction.Proposed);
                continue;
            }

            var proposal = proposals[change.Proposal];
            proposals[change.Proposal] = proposal with { Status = change.Action };
            if (change.Action == MemoryAction.Approved)
            {
                approved.Add(new(proposal.Id, proposal.Content, proposal.By, change.Time, change.By, change.Revision));
                Revision = change.Revision;
            }
        }
    }

    /// <summary>Every proposal and decision, in order.</summary>
    public IReadOnlyList<MemoryChange> Log { get; }

    /// <summary>The revision of the last approved change, or 0 while memory is empty. A conversation's memory is as of a revision.</summary>
    public long Revision { get; }

    /// <summary>Every proposal, by number.</summary>
    public IReadOnlyDictionary<long, Proposal> Proposals => proposals;

    /// <summary>The proposals waiting for a decision.</summary>
    public IReadOnlyList<Proposal> Pending => [.. proposals.Values.Where(proposal => proposal.Pending)];

    /// <summary>Every approved change in order, including those since replaced.</summary>
    public IReadOnlyList<MemoryEntry> Entries => approved;

    /// <summary>The entries that are memory at a revision: those approved by then, and not replaced by then.</summary>
    public IReadOnlyList<MemoryEntry> Current(long revision = long.MaxValue)
    {
        var known = approved.Where(entry => entry.Revision <= revision).ToList();
        var replaced = known.SelectMany(entry => entry.Content.Replaced).ToHashSet();
        return [.. known.Where(entry => !replaced.Contains(entry.Id))];
    }

    /// <summary>What memory adds to the prefix at a revision (MEM-01); empty when memory is.</summary>
    public string Text(long revision = long.MaxValue) => ProjectMemory.Render(Current(revision));
}
