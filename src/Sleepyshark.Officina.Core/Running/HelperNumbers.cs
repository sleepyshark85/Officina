using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Events;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// The numbers of a run's helpers (TEAM-07), which their ids end with, as in <c>researcher[dev.3]</c>. They are the run's, so an
/// id never repeats within the run, and a resumed run goes on after the numbers its events hold.
/// </summary>
internal sealed partial class HelperNumbers
{
    private int last;

    /// <summary>Goes on after the numbers of the run's events, for a run that resumes.</summary>
    public void After(IEnumerable<CoreEvent> events) =>
        last = events.Select(coreEvent => Number().Match(coreEvent.Agent)).Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty().Max();

    public int Next() => Interlocked.Increment(ref last);

    [GeneratedRegex(@"\.(\d+)\]$")]
    private static partial Regex Number();
}
