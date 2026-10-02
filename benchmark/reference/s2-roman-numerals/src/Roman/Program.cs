using System.Globalization;
using Roman.Core;

const string Usage = "usage: roman to <number> | roman from <numeral>";

if (args is not [("to" or "from") and var command, var value])
{
    Console.Error.WriteLine(Usage);
    return 2;
}

try
{
    if (command == "to")
    {
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            throw new FormatException($"{value} is not a whole number.");
        }

        Console.WriteLine(RomanNumerals.ToRoman(number));
    }
    else
    {
        Console.WriteLine(RomanNumerals.FromRoman(value).ToString(CultureInfo.InvariantCulture));
    }

    return 0;
}
catch (FormatException exception)
{
    Console.Error.WriteLine($"roman: {exception.Message}");
    return 2;
}
