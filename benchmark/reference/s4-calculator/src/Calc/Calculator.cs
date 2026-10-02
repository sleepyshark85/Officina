using System.Globalization;

namespace Calc;

public sealed class CalcException(string message) : Exception(message);

/// <summary>
/// Evaluates an arithmetic expression with decimal arithmetic. Precedence, lowest first: <c>+ -</c>, <c>* / %</c>, unary minus,
/// <c>^</c> (right-associative, so <c>-2^2</c> is <c>-4</c>).
/// </summary>
public sealed class Calculator
{
    private readonly string text;
    private int position;

    private Calculator(string text) => this.text = text;

    public static decimal Evaluate(string expression)
    {
        var calculator = new Calculator(expression);
        var value = calculator.Sum();
        calculator.SkipSpaces();
        if (calculator.position < expression.Length)
        {
            throw calculator.Error($"unexpected '{expression[calculator.position]}'");
        }

        return value;
    }

    public static string Format(decimal value) =>
        Math.Round(value, 10, MidpointRounding.AwayFromZero).ToString("0.##########", CultureInfo.InvariantCulture);

    private decimal Sum()
    {
        var value = Product();
        while (Next() is '+' or '-')
        {
            var op = text[position++];
            var right = Product();
            value = Checked(() => op == '+' ? value + right : value - right);
        }

        return value;
    }

    private decimal Product()
    {
        var value = Unary();
        while (Next() is '*' or '/' or '%')
        {
            var op = text[position++];
            var right = Unary();
            if (op != '*' && right == 0)
            {
                throw new CalcException("division by zero");
            }

            value = Checked(() => op switch { '*' => value * right, '/' => value / right, _ => value % right });
        }

        return value;
    }

    private decimal Unary()
    {
        if (Next() == '-')
        {
            position++;
            return -Unary();
        }

        return Power();
    }

    private decimal Power()
    {
        var value = Primary();
        if (Next() == '^')
        {
            position++;
            var exponent = Unary();
            return Raise(value, exponent);
        }

        return value;
    }

    private decimal Primary()
    {
        var next = Next();
        if (next == '(')
        {
            position++;
            var value = Sum();
            if (Next() != ')')
            {
                throw Error(position < text.Length ? $"expected ')' but found '{text[position]}'" : "expected ')'");
            }

            position++;
            return value;
        }

        if (next is not null && (char.IsAsciiDigit(next.Value) || next == '.'))
        {
            var start = position;
            while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] == '.'))
            {
                position++;
            }

            var number = text[start..position];
            if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                position = start;
                throw Error($"'{number}' is not a number");
            }

            return value;
        }

        throw Error(next is null ? "expected a number" : $"unexpected '{next}'");
    }

    private decimal Raise(decimal value, decimal exponent)
    {
        if (exponent == decimal.Truncate(exponent) && Math.Abs(exponent) <= 10_000)
        {
            var result = 1m;
            for (var n = 0; n < Math.Abs(exponent); n++)
            {
                result = Checked(() => result * value);
            }

            if (exponent < 0)
            {
                if (result == 0)
                {
                    throw new CalcException("division by zero");
                }

                result = 1 / result;
            }

            return result;
        }

        var power = Math.Pow((double)value, (double)exponent);
        if (double.IsNaN(power) || double.IsInfinity(power))
        {
            throw Error("the power has no real result");
        }

        return Checked(() => (decimal)power);
    }

    private decimal Checked(Func<decimal> compute)
    {
        try
        {
            return compute();
        }
        catch (OverflowException)
        {
            throw Error("the result is too large");
        }
    }

    private char? Next()
    {
        SkipSpaces();
        return position < text.Length ? text[position] : null;
    }

    private void SkipSpaces()
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    private CalcException Error(string reason) => new($"position {position + 1}: {reason}");
}
