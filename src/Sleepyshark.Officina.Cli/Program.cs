using System.Collections;
using Sleepyshark.Officina.Cli;

var variables = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
{
    variables[(string)variable.Key] = (string?)variable.Value ?? "";
}

return await SofCommandLine.RunAsync(args, new SofEnvironment(Console.Out, Console.Error, Environment.CurrentDirectory, variables) { In = Console.In });
