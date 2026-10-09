# Officina for Go

The Go implementation of Officina: the same requirements ([`REQUIREMENTS.md`](../REQUIREMENTS.md)) and architecture
([`ARCHITECTURE.md`](../ARCHITECTURE.md)) as the .NET one at the repository root, written as idiomatic Go.

Status: in progress. Go S01 has created the module, its packages (documentation only so far) and the checks. See
[`docs/plan/phase-1.md`](../docs/plan/phase-1.md) for the slices (the Go column),
[`docs/implementations/go.md`](../docs/implementations/go.md) for the decisions and
[`docs/traceability.md`](docs/traceability.md) for the tests of each requirement.

Layout (G2, G3):

| Path | Holds |
|---|---|
| `go.mod` | Module `github.com/sleepyshark85/officina/go` |
| `officina/` | The core: agent, run loop, conversation, tool pipeline, memory, audit, built-in stores |
| `officina/claude/` | The Claude adapter, the only importer of the Anthropic SDK |
| `officina/mcp/` | The MCP client (stdio and Streamable HTTP) |
| `officina/officinatest/` | The test kit: scripted model and approver, fake MCP server, prefix stability check |
| `dependencies_test.go` | The dependency check (TEST-05), with fixture modules in `testdata/dependencies/` |
| `cmd/bookshop/`, `internal/bookshop/` | Bookshop Assistant (from Go S06) |
| `examples/` | The GEN-06 samples (Go S13) |
| `docs/` | Go design notes and traceability |

## Build and test

Needs the Go version in `go.mod`. The tests need no API key and no network. Run from this directory:

```sh
go build ./...
go test -race -shuffle=on ./...
```

The quality gates, as CI's `go-quality` job runs them, need
[golangci-lint](https://golangci-lint.run/) v2 and govulncheck:

```sh
gofmt -l .                  # lists unformatted files; must print nothing
go vet ./...
golangci-lint run           # go/.golangci.yml, goimports included
go mod tidy -diff
govulncheck ./...
```

Mutation testing, report only until Go S05 sets a threshold:

```sh
go install github.com/go-gremlins/gremlins/cmd/gremlins@v0.6.0
gremlins unleash .
```

CI is [`.github/workflows/go.yml`](../.github/workflows/go.yml), on every pull request: `go-ubuntu` and `go-windows`
(the tests, with the race detector), `go-quality` (the gates above) and `go-mutation`. Before a commit that stages Go
code, a hook runs `gofmt`, `go vet`, `golangci-lint` and `go build`; before a push that changes Go code, the tests.
