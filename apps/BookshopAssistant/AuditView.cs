using System.Globalization;
using System.Text;
using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>
/// What <c>/audit</c> shows (APP-16): a session's entries in order, grouped by run, each run with a link to its trace in
/// the telemetry dashboard (APP-20), and each run's end with its tokens and cost.
/// </summary>
public static class AuditView
{
    public static string Format(string session, IReadOnlyList<AuditEntry> entries, Uri dashboard, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(dashboard);
        if (entries.Count == 0)
        {
            return $"No audit entries for session {session}.";
        }

        var text = new StringBuilder($"Audit of session {session}:\n");
        var runs = entries.GroupBy(entry => entry.Run).ToList();
        for (var index = 0; index < runs.Count; index++)
        {
            var trace = runs[index].Select(entry => entry.TraceId).FirstOrDefault(id => id is not null);
            text.Append(CultureInfo.InvariantCulture, $"Run {index + 1}, trace: {(trace is null ? "none recorded" : new Uri(dashboard, $"traces/detail/{trace}").ToString())}\n");
            foreach (var entry in runs[index])
            {
                var line = string.Create(
                    CultureInfo.InvariantCulture, $"  {TimeZoneInfo.ConvertTime(entry.Time, zone):HH:mm:ss}  {entry.Kind,-16}  {entry.Tool,-20}  {Outcome(entry)}");
                text.Append(line.TrimEnd()).Append('\n');
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string Outcome(AuditEntry entry) => entry switch
    {
        { Kind: AuditKind.RunEnded, Usage: { } usage } => string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.Outcome}  tokens: {usage.Input + usage.CacheRead + usage.CacheWrite:N0} in ({usage.CacheRead:N0} cached), {usage.Output:N0} out, ${entry.Cost ?? 0:0.0000}"),
        { Kind: AuditKind.ApprovalAnswered, Detail: { } reason } => $"{entry.Outcome}: {reason}",
        { Kind: AuditKind.ToolEnded, Duration: { } duration } => string.Create(CultureInfo.InvariantCulture, $"{entry.Outcome}  {duration.TotalMilliseconds:N0} ms"),
        { Kind: AuditKind.Compacted or AuditKind.Cleared } => entry.Detail ?? "",
        _ => entry.Outcome ?? "",
    };
}
