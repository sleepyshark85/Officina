Build `wc`, a .NET 10 console program in `src/WordCount`, with its unit tests in `tests/WordCount.Tests`. Given file paths
it prints, for each file, its line, word and byte counts and its path, separated by single spaces, and when there is more
than one file a last line with the totals and the word `total`; with no path it reads standard input and prints the counts
alone. A word is a run of characters other than whitespace; a line is counted for each newline character. The options `-l`,
`-w` and `-c` (which may be combined, as `-lw`) print only the chosen counts, always in the order lines, words, bytes. A file
that does not exist is reported on standard error as `wc: <path>: No such file`, the other files are still counted, and the
exit code is 1; otherwise it is 0.
