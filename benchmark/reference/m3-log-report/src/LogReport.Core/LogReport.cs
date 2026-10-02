using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogReport.Core;

/// <summary>One request of a Common Log Format line.</summary>
public sealed record Request(string Host, DateTimeOffset Time, string Path, int Status, long Bytes);

/// <summary>Which requests the report keeps: an instant range <c>[From, To)</c> and a status code or class.</summary>
public sealed record Filter(DateTimeOffset? From = null, DateTimeOffset? To = null, string? Status = null)
{
    public bool Keeps(Request request) =>
        (From is null || request.Time >= From) && (To is null || request.Time < To)
        && (Status is null || (Status.EndsWith("xx", StringComparison.Ordinal) ? request.Status / 100 == Status[0] - '0' : request.Status.ToString(CultureInfo.InvariantCulture) == Status));

    public static bool IsStatus(string status) => Regex.IsMatch(status, "^([1-5]xx|[1-5][0-9][0-9])$");
}

public static partial class CommonLog
{
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>The request of a line; null when the line is not in the Common Log Format.</summary>
    public static Request? Parse(string line)
    {
        var match = Line().Match(line);
        if (!match.Success || Time(match.Groups["time"].Value) is not { } time)
        {
            return null;
        }

        var path = match.Groups["path"].Value;
        var query = path.IndexOf('?', StringComparison.Ordinal);
        var bytes = match.Groups["bytes"].Value;
        return new(match.Groups["host"].Value, time, query < 0 ? path : path[..query], int.Parse(match.Groups["status"].Value, CultureInfo.InvariantCulture),
            bytes == "-" ? 0 : long.Parse(bytes, CultureInfo.InvariantCulture));
    }

    /// <summary>Reads <c>10/Oct/2030:13:55:36 +0000</c>.</summary>
    private static DateTimeOffset? Time(string text)
    {
        var match = TimeFormat().Match(text);
        var month = Array.IndexOf(Months, match.Groups["month"].Value) + 1;
        if (!match.Success || month == 0)
        {
            return null;
        }

        int Number(string group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
        try
        {
            var offset = new TimeSpan(Number("oh"), Number("om"), 0);
            return new DateTimeOffset(Number("year"), month, Number("day"), Number("hour"), Number("minute"), Number("second"), match.Groups["sign"].Value == "-" ? -offset : offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex("""^(?<host>\S+) \S+ \S+ \[(?<time>[^\]]+)\] "\S+ (?<path>\S+)(?: \S+)?" (?<status>\d{3}) (?<bytes>\d+|-)\s*$""")]
    private static partial Regex Line();

    [GeneratedRegex(@"^(?<day>\d{2})/(?<month>[A-Za-z]{3})/(?<year>\d{4}):(?<hour>\d{2}):(?<minute>\d{2}):(?<second>\d{2}) (?<sign>[+-])(?<oh>\d{2})(?<om>\d{2})$")]
    private static partial Regex TimeFormat();
}

/// <summary>The report, built one line at a time, so a log of any size is never held in memory.</summary>
public sealed class Report(Filter filter, int top)
{
    private readonly Dictionary<int, long> statuses = [];
    private readonly Dictionary<string, long> paths = new(StringComparer.Ordinal);
    private readonly HashSet<string> hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> perHour = new(StringComparer.Ordinal);

    public long Requests { get; private set; }

    public long Bytes { get; private set; }

    public long Malformed { get; private set; }

    public void Add(string line)
    {
        if (CommonLog.Parse(line) is not { } request)
        {
            if (line.Trim().Length > 0)
            {
                Malformed++;
            }

            return;
        }

        if (!filter.Keeps(request))
        {
            return;
        }

        Requests++;
        Bytes += request.Bytes;
        statuses[request.Status] = statuses.GetValueOrDefault(request.Status) + 1;
        paths[request.Path] = paths.GetValueOrDefault(request.Path) + 1;
        hosts.Add(request.Host);
        var hour = request.Time.UtcDateTime.ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture);
        perHour[hour] = perHour.GetValueOrDefault(hour) + 1;
    }

    public IEnumerable<(string Path, long Count)> TopPaths =>
        paths.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal).Take(top).Select(entry => (entry.Key, entry.Value));

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("requests", Requests);
            json.WriteNumber("bytes", Bytes);
            json.WriteStartObject("statuses");
            foreach (var (status, count) in statuses.OrderBy(entry => entry.Key))
            {
                json.WriteNumber(status.ToString(CultureInfo.InvariantCulture), count);
            }

            json.WriteEndObject();
            json.WriteStartArray("topPaths");
            foreach (var (path, count) in TopPaths)
            {
                json.WriteStartObject();
                json.WriteString("path", path);
                json.WriteNumber("count", count);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteNumber("hosts", hosts.Count);
            json.WriteStartObject("perHour");
            foreach (var (hour, count) in perHour.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                json.WriteNumber(hour, count);
            }

            json.WriteEndObject();
            json.WriteNumber("malformed", Malformed);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string ToText()
    {
        var text = new StringBuilder();
        void Line(string name, object value) => text.AppendLine(CultureInfo.InvariantCulture, $"{name,-12}{value}");
        Line("Requests", Requests);
        Line("Bytes", Bytes);
        Line("Hosts", hosts.Count);
        Line("Malformed", Malformed);
        text.AppendLine("Statuses");
        foreach (var (status, count) in statuses.OrderBy(entry => entry.Key))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {status,-10}{count,10}");
        }

        var width = Math.Max(10, paths.Count == 0 ? 0 : TopPaths.Max(entry => entry.Path.Length));
        text.AppendLine("Top paths");
        foreach (var (path, count) in TopPaths)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {path.PadRight(width)}{count,10}");
        }

        text.AppendLine("Per hour (UTC)");
        foreach (var (hour, count) in perHour.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {hour,-13}{count,10}");
        }

        return text.ToString();
    }
}
