Build a link shortener in ASP.NET Core on .NET 10, in `src/Shortener`, storing its data with SQLite in the file that the
environment variable `SHORTENER_DB` names and listening on `http://127.0.0.1:` and the port in `PORT`, with tests in
`tests/Shortener.Tests`. `POST /api/links` with `{ "url": "..." }` and an optional `code` answers 201 with `{ "code", "url",
"shortUrl" }`: the URL must be an absolute `http` or `https` URL, and a code given must be 3 to 32 letters, digits, `-` or
`_` and not already taken (409 if it is); a generated code is 7 letters and digits. `GET /{code}` answers 302 to the URL and
counts the visit, or 404; `GET /api/links/{code}` answers the link with its `visits` count and `lastVisitedAt` (null before
the first visit); `DELETE /api/links/{code}` answers 204, after which the code redirects nowhere and cannot be reused.
`GET /api/links` lists the links, newest first. Requests for the API need the header `X-Api-Key` with the value of the
environment variable `SHORTENER_KEY`, or answer 401; redirects need no key. Invalid input answers 400 with a JSON reason.
Everything survives a restart.
