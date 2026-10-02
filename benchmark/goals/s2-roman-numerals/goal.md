Build `roman`, a .NET 10 console program in `src/Roman` over a class library in `src/Roman.Core`, with unit tests in
`tests/Roman.Tests`. `roman to <number>` prints the Roman numeral of a whole number from 1 to 3999 in its standard, shortest
form (`1994` is `MCMXCIV`), and `roman from <numeral>` prints the number of a numeral, upper or lower case. A number outside
1 to 3999, something that is not a whole number, or a numeral that is not in standard form (such as `IIII`, `VX` or `IC`)
prints a one-line reason on standard error and exits with code 2; a missing or unknown command prints the usage and exits
with code 2 too. Every value converts back to itself.
