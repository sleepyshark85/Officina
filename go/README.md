# Officina for Go

The Go implementation of Officina: the same requirements ([`REQUIREMENTS.md`](../REQUIREMENTS.md)) and architecture
([`ARCHITECTURE.md`](../ARCHITECTURE.md)) as the .NET one at the repository root, written as idiomatic Go.

Status: in progress. The core's run loop (Go S03), the Claude adapter (Go S04), tools and audit (Go S05) and the
Bookshop Assistant console (Go S06) are in; telemetry, sessions, memory and MCP follow. See
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
| `examples/` | `hello`, a live chat (Go S04); the GEN-06 samples (Go S13) |
| `docs/` | Go design notes and traceability |

## Build and test

Needs the Go version in `go.mod`. The tests need no API key and no network. On Linux with Docker, Bookshop
Assistant's tests run against PostgreSQL in a container (testcontainers-go), started once per run; elsewhere they skip
and say why, and in CI a missing Docker fails them. Run from this directory:

```sh
go build ./...
go test -race -shuffle=on ./...
```

The hello example chats live with Claude Opus 5.5; it needs `ANTHROPIC_API_KEY` and costs a few cents. Its status
line shows cache reads from the second message:

```sh
go run ./examples/hello
```

## Bookshop Assistant

The reference application: a console chatbot for bookshop staff over the same PostgreSQL database as the .NET one,
from the compose file, schema and seed in [`apps/BookshopAssistant/`](../apps/BookshopAssistant/). It needs Docker
and `ANTHROPIC_API_KEY`; a reply costs a few cents.

```sh
(cd ../apps/BookshopAssistant && docker compose up --detach --wait postgres)
go run ./cmd/bookshop
```

It asks your name, then takes messages, such as *Order the two cheapest fantasy books in stock for Alice Martin and
tell me the total*. Replies stream with each tool call shown; a change asks for your approval with its exact input.
Ctrl+C stops a reply in progress, and the session goes on; `/help` lists the commands, `/quit` leaves.
`BOOKSHOP_DATABASE`, a PostgreSQL connection string, names another database, such as one on another port:
`postgres://bookshop:shelf-demo-41@localhost:5433/bookshop`. `docker compose down -v` deletes the data, so the next
start seeds afresh.

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
