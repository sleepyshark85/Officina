using Roman.Core;
using Xunit;

namespace Roman.Tests;

public class RomanNumeralsTests
{
    [Theory]
    [InlineData(1994, "MCMXCIV")]
    [InlineData(3999, "MMMCMXCIX")]
    public void Converts_both_ways(int number, string numeral)
    {
        Assert.Equal(numeral, RomanNumerals.ToRoman(number));
        Assert.Equal(number, RomanNumerals.FromRoman(numeral.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("IIII")]
    [InlineData("VX")]
    [InlineData("IC")]
    public void Refuses_numerals_not_in_standard_form(string numeral) =>
        Assert.Throws<FormatException>(() => RomanNumerals.FromRoman(numeral));
}
