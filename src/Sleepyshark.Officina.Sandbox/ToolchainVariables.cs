namespace Sleepyshark.Officina.Sandbox;

/// <summary>
/// The environment variables every sandboxed command gets, on Linux and on Windows, so that toolchains build in the sandbox and
/// write only what the command does. Only the toolchains they name read them, so every command gets them, whatever it runs:
/// a command line can reach a toolchain through a script or make as well as by its name.
/// </summary>
internal static class ToolchainVariables
{
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // MSBuild reads variables as properties. Source Link reads .git, which the sandbox hides, and fails every .NET build.
        ["EnableSourceControlManagerQueries"] = "false",

        // The .NET SDK's first-run banner, telemetry, development certificate and workload check, and NuGet's certificate
        // revocation checks, which reach hosts the sandbox refuses.
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
        ["DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK"] = "true",
        ["NUGET_CERT_REVOCATION_MODE"] = "offline",
    };
}
