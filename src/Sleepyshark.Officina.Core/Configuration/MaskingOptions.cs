using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Masking of personal data in work from outside the core (ING-02, ING-06).</summary>
public sealed record MaskingOptions
{
    [Setting("Whether masking is on. Turn it off where it would corrupt the content, such as source code.", Example = "false")]
    public bool Enabled { get; init; } = true;

    [Setting("What is masked: regular expressions by name, which replace the built-in ones for email addresses, phone numbers and payment card numbers. Each match becomes a token such as `[email-1]`, the same for the same value throughout the run. Names may use letters, digits and underscores.",
        Example = """{ "email": "[^@\\s]+@[^@\\s]+", "employeeId": "EMP-[0-9]{6}" }""")]
    public IReadOnlyDictionary<string, string>? Patterns { get; init; }

    // Tried in order at each position, so a card number is not taken for a phone number.
    internal static IReadOnlyDictionary<string, string> BuiltInPatterns { get; } = new Dictionary<string, string>
    {
        ["email"] = @"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}",
        ["card"] = @"(?<!\w)\d([ -]?\d){12,18}(?!\w)",
        ["phone"] = @"(?<![\w+])\+?\d([ ().-]?\d){8,14}(?!\w)",
    };

    /// <summary>
    /// Whether the text holds a masking token, such as <c>[email-1]</c>: one of the patterns' names, a dash and a number, in
    /// brackets. A token stands for a value the text does not hold.
    /// </summary>
    public bool HoldsToken(string text) =>
        Regex.IsMatch(text, $@"\[({string.Join('|', (Patterns ?? BuiltInPatterns).Keys.Select(Regex.Escape))})-\d+\]");
}
