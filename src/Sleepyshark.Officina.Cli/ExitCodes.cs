namespace Sleepyshark.Officina.Cli;

public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>The configuration has errors.</summary>
    public const int Invalid = 1;

    /// <summary>The command line is wrong, or names something the configuration does not have.</summary>
    public const int Usage = 2;
}
