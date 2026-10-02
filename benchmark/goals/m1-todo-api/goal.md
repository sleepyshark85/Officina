Build a to-do REST API in ASP.NET Core on .NET 10, in `src/TodoApi`, storing its data with SQLite in the file that the
environment variable `TODO_DB` names (created if missing) and listening on `http://127.0.0.1:` and the port in `PORT`, with
unit and integration tests in `tests/TodoApi.Tests`. A to-do has an `id` (a number the service assigns), a `title` (required,
1 to 200 characters after trimming), `done` (false at first), an optional `due` date (`yyyy-MM-dd`) and `createdAt`.
`POST /todos` creates one and answers 201 with it and a `Location` header; `GET /todos/{id}` reads one; `PATCH /todos/{id}`
changes any of `title`, `done` and `due` and answers it; `DELETE /todos/{id}` answers 204. `GET /todos` lists them by id, filtered by
`done=true|false` and by `dueBefore=<date>` (due strictly before it), and paged with `page` (from 1) and `pageSize` (1 to 100, 20 by default), as
`{ "items": [...], "total": n }`. Invalid input answers 400 with a JSON body whose `errors` object names each invalid field;
an unknown id answers 404. Everything survives a restart of the service.
