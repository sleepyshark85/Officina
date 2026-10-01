namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>One value reached by <see cref="SettingWalker"/>.</summary>
/// <param name="Path">The setting's path, such as <c>agents.developer.model</c>.</param>
/// <param name="Setting">The declaration of a property, or null for a named entry or list item.</param>
/// <param name="EntryName">The name of a named entry, or null.</param>
/// <param name="Value">The value, or null when unset.</param>
internal sealed record SettingVisit(string Path, SettingAttribute? Setting, string? EntryName, object? Value);
