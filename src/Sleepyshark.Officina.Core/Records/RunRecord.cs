using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Records;

/// <summary>
/// A run's record, shared by its agents, as one agent sees it (REC-01). Tools and gates read it (TOOL-06). Only the
/// core's record tools change it, through <see cref="ProposeAsync"/>, the one controlled update step (REC-02).
/// </summary>
public sealed partial class RunRecord
{
    private readonly IRecordStore store;
    private readonly string? tenant;
    private readonly string agent;
    private readonly string? task;
    private readonly TimeProvider time;
    private readonly Masker? masker;

    /// <param name="store">Where the record is kept.</param>
    /// <param name="context">The run, and the agent its updates are attributed to.</param>
    /// <param name="time">The clock for entry times.</param>
    internal RunRecord(IRecordStore store, ToolContext context, TimeProvider time)
    {
        this.store = store;
        tenant = context.Caller.Tenant;
        RunId = context.RunId;
        agent = context.Agent;
        task = context.TaskId;
        this.time = time;
        masker = context.Masker;
    }

    public string RunId { get; }

    /// <summary>Masks text with the run's masking, if it is on, before it reaches the record (ING-02).</summary>
    internal string Mask(string text) => masker?.Mask(text) ?? text;

    /// <summary>The record as it is now, in revision order.</summary>
    public ValueTask<IReadOnlyList<RecordEntry>> ReadAsync(CancellationToken ct) => store.ReadAsync(tenant, RunId, ct);

    /// <summary>
    /// Validates a proposal against the record as it is, and appends it as the next revision, attributed to the agent.
    /// When another update takes that revision first, it validates again against the record as it is then, so no update
    /// overwrites another (REC-04). A proposal identical to an entry is not added again.
    /// </summary>
    /// <returns>What the agent is told: the revision and any conflicts (REC-03), or why the proposal is rejected.</returns>
    internal async Task<(bool Accepted, string Text)> ProposeAsync(RecordItem item, CancellationToken ct)
    {
        while (true)
        {
            var entries = await ReadAsync(ct).ConfigureAwait(false);
            if (Problem(entries, item) is { } problem)
            {
                return (false, problem);
            }

            if (entries.FirstOrDefault(entry => entry.Item == item) is { } same)
            {
                return (true, $"Already in the record: {Describe(same, entries)}");
            }

            var added = new RecordEntry(RunId, entries.Count == 0 ? 1 : entries[^1].Revision + 1, agent, time.GetUtcNow(), item, task);
            if (await store.TryAppendAsync(tenant, added, ct).ConfigureAwait(false))
            {
                return (true, $"Recorded: {Describe(added, [.. entries, added])}");
            }
        }
    }

    /// <summary>
    /// An entry as agents read it, in the volatile context (CTX-07) and in the record tools' results. Each fact has its
    /// source and as-of time, and every conflict is marked, so both sides are kept and reported (REC-03).
    /// </summary>
    internal static string Describe(RecordEntry entry, IReadOnlyList<RecordEntry> record)
    {
        var text = entry.Item switch
        {
            Fact fact => $"fact {fact.Subject} = {fact.Value} (source: {fact.Source}; as of {fact.AsOf ?? entry.Time:u})",
            Finding finding => $"finding: {finding.Text}",
            Decision decision => $"decision on {decision.Subject}: {decision.Choice}, because {decision.Reason} (by {entry.Agent}"
                + (decision.Replaces is { } replaced ? $"; replaces r{replaced})" : ")"),
            Citation citation => $"citation [cite:{citation.Id}]: {citation.Document}, {citation.Location}: \"{citation.Quote}\"",
            _ => entry.Item.Kind,
        };
        var conflicts = record.Where(other => Conflict(entry, other, record)).Select(other => $"r{other.Revision}").ToList();
        return $"r{entry.Revision} {text}{(conflicts.Count > 0 ? $" CONFLICTS WITH {string.Join(", ", conflicts)}" : "")}";
    }

    /// <summary>The ids a text cites, as <c>[cite:id]</c>.</summary>
    internal static IReadOnlyList<string> Cited(string text) => [.. CitePattern().Matches(text).Select(match => match.Groups[1].Value).Distinct()];

    /// <summary>The first id a text cites that is not a citation in the record, if any.</summary>
    internal static string? Unresolved(string text, IReadOnlyList<RecordEntry> record) =>
        Cited(text).FirstOrDefault(id => !record.Any(entry => entry.Item is Citation citation && citation.Id == id));

    /// <summary>Why a proposal is rejected (REC-02): a cited id that does not resolve, a taken citation id, or a replaced revision that is not a decision.</summary>
    private static string? Problem(IReadOnlyList<RecordEntry> record, RecordItem item)
    {
        var text = item switch
        {
            Fact fact => $"{fact.Value} {fact.Source}",
            Finding finding => finding.Text,
            Decision decision => $"{decision.Choice} {decision.Reason}",
            _ => "",
        };
        if (Unresolved(text, record) is { } unresolved)
        {
            return $"it cites {unresolved}, which is not a citation in the record. Record the citation first.";
        }

        if (item is Citation citation && record.Any(entry => entry.Item is Citation taken && taken.Id == citation.Id && taken != citation))
        {
            return $"the citation id {citation.Id} is taken. Use another id.";
        }

        return item is Decision { Replaces: { } replaced } && !record.Any(entry => entry.Revision == replaced && entry.Item is Decision)
            ? $"r{replaced} is not a decision in the record."
            : null;
    }

    /// <summary>
    /// Facts with the same subject and different values conflict, as do current decisions with the same subject and
    /// different choices. A replaced decision is not current, so it conflicts with nothing.
    /// </summary>
    internal static bool Conflict(RecordEntry entry, RecordEntry other, IReadOnlyList<RecordEntry> record) => (entry.Item, other.Item) switch
    {
        (Fact one, Fact two) => one.Subject == two.Subject && one.Value != two.Value,
        (Decision one, Decision two) => one.Subject == two.Subject && one.Choice != two.Choice && Current(entry, record) && Current(other, record),
        _ => false,
    };

    private static bool Current(RecordEntry decision, IReadOnlyList<RecordEntry> record) =>
        !record.Any(entry => entry.Item is Decision { Replaces: { } replaced } && replaced == decision.Revision);

    [GeneratedRegex(@"\[cite:([^\]]+)\]")]
    private static partial Regex CitePattern();
}
