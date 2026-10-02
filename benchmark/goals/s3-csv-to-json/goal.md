Build `csv2json`, a .NET 10 console program in `src/CsvToJson`, with unit tests in `tests/CsvToJson.Tests`. It reads CSV as
RFC 4180 defines it from standard input, or from the file named by its one argument, and writes to standard output a JSON
array with an object per record, whose keys are the header's field names in order and whose values are the fields as
strings. Fields may be quoted, and a quoted field may hold commas, line breaks and doubled quotes; lines may end with LF or
CRLF, and a last empty line is ignored. With `--types`, a field that is a whole or decimal number becomes a JSON number and
`true` or `false` a JSON boolean, and an empty field becomes `null`. A record with a different number of fields than the
header is an error that names its line number on standard error, and the exit code is 1 with nothing written to standard
output.
