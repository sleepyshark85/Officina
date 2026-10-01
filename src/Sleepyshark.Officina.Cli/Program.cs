using System.Collections;
using Sleepyshark.Officina.Cli;

var variables = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
{
    variables[(string)variable.Key] = (string?)variable.Value ?? "";
}

return SofCommandLine.Run(args, new SofConsole(Console.Out, Console.Error, Environment.CurrentDirectory, variables));
