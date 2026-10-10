# Officina in Go — working rules

Applies to everything under `go/`, on top of [`docs/conventions.md`](../docs/conventions.md). Decisions:
[`docs/implementations/go.md`](../docs/implementations/go.md). Plan: [`docs/plan/phase-1.md`](../docs/plan/phase-1.md).

**These rules are strict.** The reviewer treats a breach as must-fix, like a design-rule breach. The references, in
order of precedence: [Effective Go](https://go.dev/doc/effective_go),
[Go Code Review Comments](https://go.dev/wiki/CodeReviewComments), the
[Google Go Style Guide](https://google.github.io/styleguide/go/) (guide, decisions, best practices), and
[Go Proverbs](https://go-proverbs.github.io/). Where a rule below is stricter, it wins. A rule is broken only with a
`//nolint:<linter> // <reason>` or a comment saying why, and the reviewer must agree with the reason.

The rules every implementation shares are in the conventions, and only there; a rule below marked *(conventions)* is
how Go realizes the one of that name.

Port the behaviour, never the shape (conventions). Go code that reads like C# (getters, `I`-prefixed interfaces, a type
per file, a container, exceptions as panics, builders for everything) is rewritten.

## Tooling (enforced by hooks and CI)

- `gofmt` and `goimports`; `go vet`; `golangci-lint run` with `.golangci.yml`; `go mod tidy` leaves no diff;
  `govulncheck ./...` clean; `go test -race -shuffle=on ./...` green. All before a commit or push, as for .NET.
- No `GOEXPERIMENT`, no `unsafe`, no `cgo`, no `init()` functions. No `reflect` outside tests, except where the core
  derives a JSON Schema from a Go type (typed tools and typed output), and only there.
- A new dependency meets the conventions' *Dependencies* row, its line in `docs/implementations/go.md`.

## Packages and API

- Package names: short, lowercase, one word, no `util`, `common`, `helpers`, `models` or `types`. No stutter:
  `officina.Agent`, not `officina.OfficinaAgent`; `claude.New`, not `claude.NewClaudeModel`.
- The conventions' minimal API, in Go: unexported by default; `internal/` for code other modules must not import.
  An exported identifier's doc comment is a full sentence starting with its name. Each package has a `doc.go`.
- **Accept interfaces, return concrete types.** Interfaces follow the conventions' contract rules and are named for
  what they do (`Approver`, `AuditSink`, `MemoryStore`), with no `I` prefix.
- Make the zero value useful, or unexported with a constructor. Constructors take required dependencies as
  arguments and optional settings in an options struct; functional options only once a package has several optional
  settings with real users. No setters on shared objects: an `Agent` is immutable once built (AGT-01).
- No getters named `GetX`: a field accessor is `X()`. Receiver names are one or two letters, the same on every method;
  a type's methods are all pointer receivers or all value receivers.
- No package-level mutable state. Constants for fixed values, per the "simplest thing" rule.
- Generics only where they remove real duplication; never to imitate C# collection types.

## Errors

- Errors are values: return them, last. Never `panic` for a failure a caller can meet; a panic means a bug. The
  core never lets a tool's panic escape: the pipeline recovers it into an error result for the model.
- Wrap with context using `%w` (`fmt.Errorf("load session %s: %w", id, err)`); check with `errors.Is` and
  `errors.As`. Sentinel errors are `ErrX` variables; error types end in `Error`. Messages are lowercase, no final
  punctuation.
- An error is handled once (conventions): returned or logged, never both. Never discard one with `_` without a
  comment saying why.
- A run's outcome is a result value (conventions' design rules), so `error` means the API misused or the environment
  broken.

## Concurrency

- `context.Context` is the first parameter of anything that blocks or does I/O, named `ctx`, never stored in a struct.
- **A goroutine's owner (conventions) waits for it with `sync.WaitGroup` and `wg.Go`;** its context stops it. No
  fire-and-forget. `goleak` in every package's `TestMain` proves it; the end-to-end tests ignore only testcontainers'
  own reaper goroutines, by name.
- The goroutine that sends on a channel closes it. A channel's known bound, for a reply's tool events, is the number of
  calls times the events per call, never "big enough".
- Prefer a mutex for guarding state and a channel for handing over work; don't use channels as locks. Copy no type
  that holds a `sync.Mutex`.
- Streams are `iter.Seq` / `iter.Seq2`; on a consumer's early `break`, the producer releases everything it started
  (conventions).

## Tests

- Table-driven tests with `t.Run` subtests, the requirement ID in the name (`TestRun_AGT05_CancelMidStream`).
  `t.Parallel()` unless a test shares a boundary fake. `t.Helper()` in helpers, `t.Cleanup` over `defer` in setup.
- Compare with `cmp.Diff(want, got)` and print `(-want +got)`; in plain messages, `got` before `want`.
  No assertion libraries.
- Black-box tests (`package officina_test`) by default; internal tests only for what the API can't reach.
- `Example` functions for each exported entry point; they run as tests and are the API docs.
- Fuzz tests (`FuzzX`) are the generated-input tests every parser and validator has (conventions); property tests
  with `rapid` for TEST-07.
- Golden files under the package's `testdata/`, updated only with `-update`, which never writes the shared top-level
  `testdata/` (conventions).

## Files

- Files are grouped by concept, not one type per file; a file over about 500 lines is a sign to split by concept.
- File names are lowercase with underscores (`tool_pipeline.go`, `tool_pipeline_test.go`).
