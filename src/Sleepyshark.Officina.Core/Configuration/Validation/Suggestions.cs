namespace Sleepyshark.Officina.Core.Configuration.Validation;

/// <summary>"Did you mean …?" for misspelt names.</summary>
public static class Suggestions
{
    /// <summary>The closest candidate within a small edit distance, as <c> Did you mean "x"?</c>, or an empty string.</summary>
    public static string DidYouMean(string name, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(candidates);
        var best = candidates
            .Select(candidate => (candidate, distance: Distance(name.ToUpperInvariant(), candidate.ToUpperInvariant())))
            .Where(match => match.candidate != name && match.distance <= Math.Max(1, name.Length / 3))
            .OrderBy(match => match.distance)
            .ThenBy(match => match.candidate, StringComparer.Ordinal)
            .Select(match => match.candidate)
            .FirstOrDefault();
        return best is null ? "" : $" Did you mean \"{best}\"?";
    }

    /// <summary>Edits between two names, counting a swap of neighbouring letters as one (optimal string alignment).</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }
}
