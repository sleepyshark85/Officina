namespace Roman.Core;

/// <summary>Converts whole numbers from 1 to 3999 to Roman numerals in standard form, and back.</summary>
public static class RomanNumerals
{
    private static readonly (int Value, string Symbol)[] Symbols =
    [
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
        (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    ];

    public static string ToRoman(int number)
    {
        if (number is < 1 or > 3999)
        {
            throw new FormatException($"{number} is outside 1 to 3999.");
        }

        var numeral = new System.Text.StringBuilder();
        foreach (var (value, symbol) in Symbols)
        {
            while (number >= value)
            {
                numeral.Append(symbol);
                number -= value;
            }
        }

        return numeral.ToString();
    }

    public static int FromRoman(string numeral)
    {
        var upper = numeral.ToUpperInvariant();
        if (upper.Length == 0)
        {
            throw new FormatException("The numeral is empty.");
        }

        var total = 0;
        for (var i = 0; i < upper.Length; i++)
        {
            var value = ValueOf(upper[i]);
            var next = i + 1 < upper.Length ? ValueOf(upper[i + 1]) : 0;
            total += value < next ? -value : value;
        }

        // Only the standard, shortest form converts back to itself.
        if (total is < 1 or > 3999 || ToRoman(total) != upper)
        {
            throw new FormatException($"{numeral} is not a Roman numeral in standard form.");
        }

        return total;
    }

    private static int ValueOf(char symbol) => symbol switch
    {
        'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, 'M' => 1000,
        _ => throw new FormatException($"'{symbol}' is not a Roman numeral symbol."),
    };
}
