using System.Reflection;

var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

if (args is ["--version"])
{
    Console.WriteLine(version);
    return 0;
}

Console.WriteLine($"sof {version} - the Officina coding team CLI. Commands arrive in later slices.");
return 0;
