namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>A setting the host passes for a run.</summary>
/// <param name="Path">The setting, as dotted names, such as <c>run.budget.cost</c>.</param>
/// <param name="Value">The value: JSON where it parses as JSON, otherwise text.</param>
/// <param name="Source">What set it, such as <c>--budget</c>, shown as its origin.</param>
public sealed record RunOption(string Path, string Value, string Source);
