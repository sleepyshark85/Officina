namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Validation messages shared by several annotations: the problem, then the fix.</summary>
internal static class Messages
{
    public const string Required = "is required but not set. Add it; it has no default.";

    /// <summary>For a budget limit, which protects INV-07.</summary>
    public const string NotZero = "must be greater than zero. A limit can be high, but never zero, negative or unlimited.";
}
