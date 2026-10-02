Build an issue tracker REST API in ASP.NET Core on .NET 10, in `src/Tracker` with its domain in `src/Tracker.Domain`, storing
its data with SQLite in the file that `TRACKER_DB` names and listening on `http://127.0.0.1:` and the port in `PORT`, with unit
and integration tests in `tests/`. `POST /users` with `{ "name", "email" }` needs no authentication and answers 201 with the
user and an API `token`, which only this answer shows (`GET /users/{id}` does not); the first user is an admin. Every other
request needs `Authorization: Bearer <token>`, or answers 401. Projects (`POST /projects` with a `key` of 2 to 10 capital
letters, unique or 409, and a `name`) have members, added with `POST /projects/{key}/members` and a `userId` by the project's
creator or an admin; only members (the creator is one) and admins see a project and its issues, others get 403, and
`GET /projects` lists only those the user sees. An issue (`POST /projects/{key}/issues`) has an id `KEY-n`, numbered per
project, a `title`, a `description`, a `type` (`bug`, `task` or `story`), a `priority` (`low`, `medium`, `high`, `critical`),
an `assigneeId` that must be a member, `labels` and a `status` that starts `open`; `PATCH /issues/{id}` changes any of the
others. The status moves only along `open → in_progress → in_review → done`, back from `in_review` to `in_progress`, and to
`closed` from any state, by `POST /issues/{id}/transitions` with `{ "to" }` (200, or 409 for another move). Every change is kept
in `GET /issues/{id}/history` as `{ "userId", "at", "field", "old", "new" }`; comments (`{ "body" }`) are added and listed at
`/issues/{id}/comments` and changed with `PUT /issues/{id}/comments/{commentId}`, only by their author (403 otherwise).
`GET /projects/{key}/issues` filters by `status`, `assigneeId`, `label`, `type` and text in `q`, sorts by `sort=priority|created|
updated`, ascending or with a `-` prefix descending, and pages with `page` and `pageSize`. Every list answers `{ "items",
"total" }`. Invalid input answers 400 with an `errors` object, an unknown resource 404, and everything survives a restart.
