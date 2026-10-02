Build `logreport`, a .NET 10 console program in `src/LogReport` over a class library in `src/LogReport.Core`, with unit tests
in `tests/LogReport.Tests`. It reads web server access logs in the Common Log Format (`host ident user [10/Oct/2030:13:55:36
+0000] "GET /path HTTP/1.1" 200 2326`) from the files given as arguments, gzip-compressed when they end in `.gz`, or from
standard input, and prints a report as JSON with: `requests` (the count), `bytes` (the sum; `-` counts as 0), `statuses` (the
count per status code, as an object keyed by the code), `topPaths` (the 10 most requested paths without their query string,
as `{ "path", "count" }`, ties broken by path in ordinal order), `hosts` (the number of distinct hosts), `perHour` (the count
per hour in UTC, keyed `yyyy-MM-ddTHH`) and `malformed` (the lines that could not be parsed, which are skipped). `--from` and
`--to`, as ISO 8601 instants, keep only the requests in `[from, to)`; `--status 4xx` (or a code, or a class from `1xx` to
`5xx`) keeps only those statuses; `--top N` changes the number of paths. `--format text` prints the same report as aligned
plain text instead. A file that cannot be read exits with code 1 and a reason on standard error. It reads a 100 MB log in
less than 30 seconds without holding it in memory.
