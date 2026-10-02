using System.Globalization;
using System.IO.Compression;
using LogReport.Core;

const string Usage = "usage: logreport [--from <instant>] [--to <instant>] [--status <code|Nxx>] [--top <n>] [--format json|text] [file ...]";

var files = new List<string>();
DateTimeOffset? from = null, to = null;
string? status = null;
var (top, format) = (10, "json");
for (var i = 0; i < args.Length; i++)
{
    var option = args[i];
    if (!option.StartsWith("--", StringComparison.Ordinal))
    {
        files.Add(option);
        continue;
    }

    if (i + 1 >= args.Length)
    {
        return Fail($"{option} needs a value.\n{Usage}", 2);
    }

    var value = args[++i];
    switch (option)
    {
        case "--from" or "--to":
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
            {
                return Fail($"{option} must be an ISO 8601 instant.", 2);
            }

            if (option == "--from") from = instant; else to = instant;
            break;
        case "--status" when Filter.IsStatus(value):
            status = value;
            break;
        case "--top" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0:
            top = n;
            break;
        case "--format" when value is "json" or "text":
            format = value;
            break;
        default:
            return Fail($"{option} {value} is not valid.\n{Usage}", 2);
    }
}

var report = new Report(new Filter(from, to, status), top);
try
{
    if (files.Count == 0)
    {
        Read(Console.In, report);
    }

    foreach (var file in files)
    {
        using var stream = File.OpenRead(file);
        using var reader = new StreamReader(file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(stream, CompressionMode.Decompress) : stream);
        Read(reader, report);
    }
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
{
    return Fail(exception.Message, 1);
}

Console.Out.Write(format == "json" ? report.ToJson() + Environment.NewLine : report.ToText());
return 0;

static void Read(TextReader reader, Report report)
{
    while (reader.ReadLine() is { } line)
    {
        report.Add(line);
    }
}

static int Fail(string reason, int code)
{
    Console.Error.WriteLine($"logreport: {reason}");
    return code;
}
