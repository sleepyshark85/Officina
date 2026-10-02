using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// The masking of one run (ING-02, ING-06). Each value a pattern matches becomes a token, such as <c>[email-1]</c>, that
/// is the same for the same value throughout the run. Only a tool configured to receive real values gets them back, in
/// its arguments; the model, history, events, audit and logs see only tokens.
/// </summary>
/// <param name="patterns">The patterns, one named group each, as <see cref="Expression"/> builds them.</param>
internal sealed class Masker(Regex patterns)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, string> tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    /// <summary>One expression for every pattern, tried in order at each position, so each value is matched once.</summary>
    public static Regex Expression(IReadOnlyDictionary<string, string> patterns) =>
        new(string.Join('|', patterns.Select(pattern => $"(?<{pattern.Key}>{pattern.Value})")), RegexOptions.ExplicitCapture);

    public string Mask(string text) => patterns.Replace(text, match =>
    {
        lock (gate)
        {
            if (!tokens.TryGetValue(match.Value, out var token))
            {
                token = $"[{match.Groups.Values.Skip(1).First(group => group.Success).Name}-{tokens.Count + 1}]";
                tokens[match.Value] = token;
                values[token] = match.Value;
            }

            return token;
        }
    });

    /// <summary>
    /// Whether the text holds a token, of this run or any other, such as <c>[email-1]</c>: one of the patterns' names, a dash and a
    /// number, in brackets.
    /// </summary>
    public bool HoldsToken(string text) =>
        Regex.IsMatch(text, $@"\[({string.Join('|', patterns.GetGroupNames().Where(name => !int.TryParse(name, out _)).Select(Regex.Escape))})-\d+\]");

    /// <summary>The arguments with each token of this run replaced by its value, just before the tool runs.</summary>
    public JsonElement Restore(JsonElement arguments)
    {
        var text = arguments.GetRawText();
        lock (gate)
        {
            foreach (var (token, value) in values)
            {
                text = text.Replace(token, JsonEncodedText.Encode(value).Value, StringComparison.Ordinal);
            }
        }

        return JsonElement.Parse(text);
    }
}
