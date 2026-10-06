namespace Sleepyshark.Officina;

/// <summary>Text helpers the run's components share.</summary>
internal static class Strings
{
    /// <summary><paramref name="text"/> cut to at most <paramref name="length"/> characters, never inside a surrogate pair.</summary>
    internal static string Cut(string text, int length) =>
        text.Length <= length ? text : text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];
}
