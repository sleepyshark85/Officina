using System.Text;
using CsvToJson;

var types = args.Contains("--types");
var files = args.Where(arg => arg != "--types").ToList();
if (files.Count > 1)
{
    Console.Error.WriteLine("usage: csv2json [--types] [file]");
    return 2;
}

try
{
    string text;
    if (files.Count == 1)
    {
        text = File.ReadAllText(files[0], Encoding.UTF8);
    }
    else
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        text = reader.ReadToEnd();
    }

    var json = Csv.ToJson(Csv.Parse(text), types);
    using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
    output.WriteLine(json);
    return 0;
}
catch (CsvException exception)
{
    Console.Error.WriteLine($"csv2json: {exception.Message}");
    return 1;
}
catch (IOException exception)
{
    Console.Error.WriteLine($"csv2json: {exception.Message}");
    return 1;
}
