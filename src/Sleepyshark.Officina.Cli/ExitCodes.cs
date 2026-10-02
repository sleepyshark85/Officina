namespace Sleepyshark.Officina.Cli;

public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>The configuration has errors.</summary>
    public const int Invalid = 1;

    /// <summary>The command line is wrong, or names something the configuration does not have.</summary>
    public const int Usage = 2;

    /// <summary>The run ended without completing: it was handed off, rejected or failed.</summary>
    public const int NotCompleted = 3;
}
