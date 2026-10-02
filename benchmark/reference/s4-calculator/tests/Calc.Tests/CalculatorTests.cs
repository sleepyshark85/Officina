using Calc;
using Xunit;

namespace Calc.Tests;

public class CalculatorTests
{
    [Theory]
    [InlineData("-2^2", "-4")]
    [InlineData("2^3^2", "512")]
    [InlineData("1/3", "0.3333333333")]
    public void Evaluates_with_precedence(string expression, string result) =>
        Assert.Equal(result, Calculator.Format(Calculator.Evaluate(expression)));

    [Fact]
    public void Names_the_position_of_an_error() =>
        Assert.StartsWith("position 5:", Assert.Throws<CalcException>(() => Calculator.Evaluate("1 + * 2")).Message);
}
