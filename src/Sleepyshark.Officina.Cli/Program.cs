using System.Collections;
using Sleepyshark.Officina.Cli;

var variables = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
{
    variables[(string)variable.Key] = (string?)variable.Value ?? "";
}

var input = TerminalReader.For(Console.In);
return await SofCommandLine.RunAsync(
    args,
    new SofEnvironment(TerminalReader.Output(Console.Out, input), Console.Error, Environment.CurrentDirectory, variables) { In = input, Signals = SofEnvironment.ProcessSignals });
