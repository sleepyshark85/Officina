Build a library catalog REST API in ASP.NET Core on .NET 10, in `src/Library`, storing its data with SQLite in the file that
the environment variable `LIBRARY_DB` names and listening on `http://127.0.0.1:` and the port in `PORT`, with tests in
`tests/Library.Tests`. It keeps authors (`id`, `name`), books (`id`, `isbn`, `title`, `year`, `authorIds`, `copies`) and
members (`id`, `name`, `email`, a unique address), each with `POST`, `GET` by id, `PUT` and `DELETE` under `/authors`, `/books` and
`/members`. An ISBN is a valid ISBN-13 (with its check digit) and unique, and a taken ISBN or email answers 409; a book names at least one existing author; an author
with books cannot be deleted (409). `POST /loans` with `{ "bookId", "memberId" }` lends a copy for 14 days (409 when no copy is
free or the member already has 3 loans or an overdue one), `POST /loans/{id}/return` returns it, and `GET /members/{id}/loans`
lists the member's open loans with their `dueOn`. `GET /books?q=` searches titles and author names, case-insensitive, with
`available=true` keeping books with a free copy, sorted by title ignoring case, paged with `page` and `pageSize` as `{ "items", "total" }`.
The service reads the current date from the environment variable `LIBRARY_TODAY` (`yyyy-MM-dd`) when it is set, for tests.
Invalid input answers 400 with an `errors` object, an unknown id 404, and everything survives a restart.
