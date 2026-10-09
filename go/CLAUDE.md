# Officina in Go — working rules

Applies to everything under `go/`, on top of [`docs/conventions.md`](../docs/conventions.md). Decisions:
[`docs/implementations/go.md`](../docs/implementations/go.md). Plan: [`docs/plan/phase-1.md`](../docs/plan/phase-1.md).

**These rules are strict.** The reviewer treats a breach as must-fix, like a design-rule breach. The references, in
order of precedence: [Effective Go](https://go.dev/doc/effective_go),
[Go Code Review Comments](https://go.dev/wiki/CodeReviewComments), the
[Google Go Style Guide](https://google.github.io/styleguide/go/) (guide, decisions, best practices), and
[Go Proverbs](https://go-proverbs.github.io/). Where a rule below is stricter, it wins. A rule is broken only with a
`//nolint:<linter> // <reason>` or a comment saying why, and the reviewer must agree with the reason.

Port the behaviour, never the C#. If Go code reads like C# (getters, `I`-prefixed interfaces, a type per file, a
container, exceptions as panics, builders for everything), rewrite it.

## Tooling (enforced by hooks and CI)

- `gofmt` and `goimports`; `go vet`; `golangci-lint run` with `.golangci.yml`; `go mod tidy` leaves no diff;
  `govulncheck ./...` clean; `go test -race -shuffle=on ./...` green. All before a commit or push, as for .NET.
- Required checks before a merge, from `.github/workflows/go.yml`: `go-changes`, `go-ubuntu`, `go-windows`,
  `go-quality`, `go-mutation`, besides the four .NET ones (every PR needs all nine).
- No `GOEXPERIMENT`, no `unsafe`, no `cgo`, no `init()` functions. No `reflect` outside tests, except where the core
  derives a JSON Schema from a Go type (typed tools and typed output), and only there.
- New dependencies need a line in `docs/implementations/go.md` saying why the standard library is not enough.

## Packages and API

- Package names: short, lowercase, one word, no `util`, `common`, `helpers`, `models` or `types`. No stutter:
  `officina.Agent`, not `officina.OfficinaAgent`; `claude.New`, not `claude.NewClaudeModel`.
- Keep the exported API minimal. Unexported by default; `internal/` for code other modules must not import. Every
  exported identifier has a doc comment, a full sentence starting with its name. Each package has a `doc.go`.
- **Accept interfaces, return concrete types.** Interfaces are small (one to three methods), defined where they are
  used, and named for what they do (`Approver`, `AuditSink`, `MemoryStore`); no `I` prefix, no interface with one
  implementation and no consumer that needs to swap it.
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
- Handle an error once: return it or log it, never both. Never discard one with `_` without a comment saying why.
- A run's outcome (completed, stopped, failed) is a result value, not an error (AGT-03); `error` is for the API
  misused or the environment broken.

## Concurrency

- `context.Context` is the first parameter of anything that blocks or does I/O, named `ctx`, never stored in a struct.
- **Every goroutine has an owner that waits for it** (`sync.WaitGroup` with `wg.Go`) and a way to stop (its
  context). No fire-and-forget. `goleak` in every package's `TestMain` proves it; the end-to-end tests ignore
  only testcontainers' own reaper goroutines, by name.
- The goroutine that sends on a channel closes it. Channels are sized from a known bound (for a reply's tool events,
  the number of calls times the events per call), never "big enough".
- Prefer a mutex for guarding state and a channel for handing over work; don't use channels as locks. Copy no type
  that holds a `sync.Mutex`.
- Streams are `iter.Seq` / `iter.Seq2`; a consumer's early `break` must release everything the producer started.

## Tests

- Table-driven tests with `t.Run` subtests and names that carry the requirement ID (`TestRun_AGT05_CancelMidStream`).
  `t.Parallel()` unless a test shares a boundary fake. `t.Helper()` in helpers, `t.Cleanup` over `defer` in setup.
- Compare with `cmp.Diff(want, got)` and print `(-want +got)`; in plain messages, `got` before `want`.
  No assertion libraries.
- Black-box tests (`package officina_test`) by default; internal tests only for what the API can't reach.
- `Example` functions for each exported entry point; they run as tests and are the API docs.
- Fuzz tests (`FuzzX`) for every parser and validator (JSON Schema subset, MCP messages, memory paths); property tests
  with `rapid` for TEST-07.
- Golden files under the package's `testdata/`, updated only with `-update`, reviewed like code. The shared files in
  the repository's top-level `testdata/` are read only: .NET reads them too, and `-update` never writes them.

## Files

- Files are grouped by concept, not one type per file; a file over about 500 lines is a sign to split by concept.
- File names are lowercase with underscores (`tool_pipeline.go`, `tool_pipeline_test.go`).
