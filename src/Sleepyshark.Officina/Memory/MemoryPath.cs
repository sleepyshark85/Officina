namespace Sleepyshark.Officina;

/// <summary>
/// The scopes and paths a memory store accepts. A scope is one path part; a path is parts separated by <c>/</c>. A part
/// is never empty, <c>.</c> or <c>..</c>, never ends with a dot or space, holds no control character and none of
/// <c>\ / : * ? " &lt; &gt; | %</c>, and is no reserved Windows device name (<c>CON</c>, <c>COM1</c>, <c>CONIN$</c>…).
/// So a path is relative, cannot climb out, and names the same file in every store.
/// </summary>
public static class MemoryPath
{
    /// <summary>The longest path, in characters.</summary>
    public const int MaxLength = 1_024;

    private const string Forbidden = "\\/:*?\"<>|%";

    private static readonly HashSet<string> Devices = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", .. "0123456789\u00b9\u00b2\u00b3".SelectMany(digit => new[] { $"COM{digit}", $"LPT{digit}" })],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string? path) => path is { Length: > 0 and <= MaxLength } && path.Split('/').All(IsPart);

    public static bool IsValidScope(string? scope) => scope is not null && IsPart(scope);

    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="scope"/>, and any <paramref name="path"/>, are valid.</summary>
    public static void Check(string scope, string? path = null)
    {
        if (!IsValidScope(scope) || (path is not null && !IsValid(path)))
        {
            throw new ArgumentException(IsValidScope(scope) ? $"'{path}' is not a valid memory path." : $"'{scope}' is not a valid memory scope.");
        }
    }

    private static bool IsPart(string part) =>
        part.Length is > 0 and <= 255 && part[^1] is not ('.' or ' ') && !part.Any(c => char.IsControl(c) || Forbidden.Contains(c))
        && !Devices.Contains(part.Split('.')[0].TrimEnd());
}
