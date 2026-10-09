# Go implementation

The second implementation, in [`go/`](../../go/): the same core, Claude and MCP packages, test kit, Bookshop Assistant
and samples as the .NET one, meeting the same [`REQUIREMENTS.md`](../../REQUIREMENTS.md) with the same acceptance
criteria. It is a rewrite from the requirements and the architecture, written as idiomatic Go, not a translation of
the C#. Working rules: [`go/CLAUDE.md`](../../go/CLAUDE.md). Plan: [`docs/plan/go-port.md`](../plan/go-port.md).

All decisions below are **proposed** until the owner confirms them; G01 and G02 check every library named here
before anything depends on it.

| # | Fills in | Decision | Reason |
|---|---|---|---|
| G1 | D1, TEST-03 | The newest stable Go when G01 starts, pinned in `go.mod` (`go` and `toolchain` lines); CI on Linux and Windows, like .NET. | Same reach as .NET |
| G2 | D8 | One module, `github.com/sleepyshark85/officina/go`. Package names are short and lowercase, not `Sleepyshark.Officina`: `officina` (core), `officina/claude`, `officina/mcp`, `officina/officinatest` (test kit, named like `net/http/httptest`). Bookshop Assistant is `cmd/bookshop` with its code in `internal/bookshop`; samples are in `examples/`. | Go names packages by what they provide; the module path carries the owner |
| G3 | Core layout | The core is **one package**, one file per concept (`agent.go`, `run.go`, `conversation.go`, `tool.go`, `pipeline.go`, `memory.go`, `audit.go`…), not a package per .NET folder. A sub-package only when it has its own user and no cycle back. | Go packages are units of API, not folders of types; splitting the core early creates import cycles and exported internals |
| G4 | MDL-02, TEST-05 | The official Anthropic Go SDK (`github.com/anthropics/anthropic-sdk-go`), imported by `officina/claude` only; a test lists every package's imports (`go list -deps`) and fails otherwise. G02 proves the beta features on it. | As .NET; the SDKs differ in what beta features they expose |
| G5 | D15 | **No dependency injection container.** Each package exposes constructors (`claude.New(…)`, `mcp.Connect(ctx, …)`) that take their dependencies explicitly; the application wires everything in one composition root (`main` calling `internal/bookshop.Build`), and tests call the same function with boundary fakes. | Go's convention is explicit wiring; a container would hide what `main` builds |
| G6 | D15, EVT-02 | Besides the standard library, the core depends only on the OpenTelemetry API modules (`go.opentelemetry.io/otel`, `…/trace`, `…/metric`), never the SDK or an exporter; it emits nothing unless the host installs a provider. Logs, where the core needs any, use `log/slog`. | Go's standard library has no tracing or metrics; OpenTelemetry tells libraries to depend on the API only |
| G7 | Q1 | `pgx` (`github.com/jackc/pgx/v5`), with `pgxpool`; plain SQL, no object mapper. | The plain driver, as in .NET |
| G8 | Q2, TEST-08 | The core's own small validator, ported; `github.com/santhosh-tekuri/jsonschema` as the reference validator, in tests only. | As .NET |
| G9 | AGT-06, MDL-05 | Provider content blocks are kept as `json.RawMessage` and replayed unchanged; conversation JSON uses the same format as .NET, so the shared golden files apply (see the plan). Only the stable `encoding/json`, no `GOEXPERIMENT`. | Byte-exact replay and TEST-02 need bytes nobody re-encodes |
| G10 | EVT-01 | A run returns its events as an `iter.Seq[RunEvent]` and its result after the sequence ends; stopping the loop early (`break`) cancels the run's tools and waits for them, as the .NET event stream does. Cancellation is a `context.Context`, always the first parameter. | Range-over-func iterators are Go's pull-free stream; `break` gives the "host stopped reading" signal without a leaked goroutine |
| G11 | TEST-01, TEST-07 | The standard `testing` package with `github.com/google/go-cmp` for comparisons; property tests with `pgregory.net/rapid` (failures print a reproducible seed); `go.uber.org/goleak` in every package's `TestMain`; `testing/synctest` for anything timed, with the clock still injected as a boundary; `testcontainers-go` for PostgreSQL. No assertion framework. | Standard-library first; goleak and the race detector catch the concurrency bugs Go makes easy |
| G12 | Quality gates | `gofmt`, `go vet`, `golangci-lint` with the linter set in `go/.golangci.yml`, `govulncheck`, `go mod tidy` with no diff, `go test -race`; mutation testing with `gremlins`, its threshold set from G05's baseline. | The tools the Go community and its style guides expect |
| G13 | Principle 11 | Core line budget: set after G03 from the measured size of the run loop; Go is wordier than C#, so expect more than .NET's 3,500. | A budget needs a measurement |
| G14 | NS-13 | Hosting helpers, when they enter, target `net/http`. | Standard library first |
