using Calc;

if (args.Length > 0)
{
    var (ok, line) = Evaluate(string.Join(' ', args));
    Console.WriteLine(line);
    return ok ? 0 : 1;
}

while (Console.ReadLine() is { } expression)
{
    Console.WriteLine(Evaluate(expression).Line);
}

return 0;

static (bool Ok, string Line) Evaluate(string expression)
{
    try
    {
        return (true, Calculator.Format(Calculator.Evaluate(expression)));
    }
    catch (CalcException exception)
    {
        return (false, $"error: {exception.Message}");
    }
}
