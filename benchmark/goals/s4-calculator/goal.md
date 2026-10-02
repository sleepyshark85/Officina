Build `calc`, a .NET 10 console program in `src/Calc`, with unit tests in `tests/Calc.Tests`. It evaluates arithmetic
expressions with decimal numbers, `+`, `-`, `*`, `/`, `%` (remainder), `^` (power, right-associative and binding tighter than
unary minus, so `-2^2` is `-4`), unary minus, parentheses and any spaces, with the usual precedence. Given an expression as its
argument it prints the result; with none it reads one expression per line from standard input and prints one result per
line, continuing after an error. Results use decimal arithmetic, are printed with up to 10 decimal places and no trailing
zeros (`1/3` is `0.3333333333`, `0.1+0.2` is `0.3`). An invalid expression prints `error: ` and the position (counted from 1)
and reason, and division by zero prints `error: division by zero`; with an argument, an error exits with code 1.
