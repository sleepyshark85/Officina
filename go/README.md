# Officina for Go

The Go implementation of Officina: the same requirements ([`REQUIREMENTS.md`](../REQUIREMENTS.md)) and architecture
([`ARCHITECTURE.md`](../ARCHITECTURE.md)) as the .NET one at the repository root, written as idiomatic Go.

Status: in progress. The core's run loop (Go S03), the Claude adapter (Go S04), tools and audit (Go S05), the
Bookshop Assistant console (Go S06), telemetry with the audit view (Go S07), sessions with budgets (Go S08), long
conversations (Go S10) and the MCP client with exports (Go S11) are in; memory follows. See
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
Assistant's tests run against PostgreSQL in a container (testcontainers-go), started once per run, and its export
tests against the filesystem MCP server, built from the compose file's `exports-server` image; elsewhere they skip
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
(cd ../apps/BookshopAssistant && ./start.sh)
go run ./cmd/bookshop
```

It asks your name, then takes messages, such as *Order the two cheapest fantasy books in stock for Alice Martin and
tell me the total*. Replies stream with each tool call shown; a change asks for your approval with its exact input.
Ctrl+C stops a reply in progress, and the session goes on; `/help` lists the commands, `/quit` leaves.
Asked to export a report, such as *Export Alice Martin's order history as CSV*, it writes the file, after your
approval, through the compose file's filesystem MCP server, into `apps/BookshopAssistant/exports/`. The application
does not start without that server.

Each conversation is a session, saved in the database's `sessions` table after every step of a reply, so a restart or
a crash loses at most the step in flight. `/sessions` lists the latest, `/resume <id>` goes on with one (with its
cache intact, and a call a crash left unanswered told to the model as interrupted), `/new` starts another. Sessions
are stored as the .NET application stores them, in the same table; one the .NET application started is refused as
another version of the assistant until both chat agents have the same tools (memory comes with
Go S09). After each reply a status line shows its tokens,
the share read from the cache, its cost and the session's; `/cost` shows the session's. A reply may spend $0.50 and a
session $5; reaching either stops the reply and says why. `BOOKSHOP_REPLY_BUDGET`, in US dollars, such as `0.01`,
lowers the reply's budget to show a stop.
`/audit` shows the session's audit trail, from the database's `audit` table, grouped by run: each entry's time,
kind, tool and outcome, approvals, and each run's tokens and cost, with a link to the run's trace.

The compose file's telemetry dashboard (the standalone Aspire dashboard, the same one .NET uses) shows each reply as a
trace (the reply, the run, its model calls and tool calls, with tokens, cache reads, cost, approval wait and errors),
the core's metrics (tokens, cost, cache hit ratio, latency, tool outcomes, approvals) and the application's logs. Open
http://localhost:18888; the application sends OTLP/gRPC to http://localhost:4317. `OTEL_EXPORTER_OTLP_ENDPOINT` and
`BOOKSHOP_DASHBOARD` name others, such as the ports a `.env` file beside the compose file chose. Telemetry holds no
message text: the audit trail is the record.
Long conversations are shortened on Claude's side: the conversation is compacted into a summary from 150,000 input
tokens, and old tool results are cleared once a request holds more than 20 tool calls and clearing frees at least
20,000 tokens. Each shows as a `~` line and an `/audit` entry. `go run ./cmd/bookshop --demo` compacts from 50,000
input tokens (Claude's minimum) and clears above 12 tool calls, to see both in a short session: four broad catalogue
searches in one message compact (about $0.30), and two turns of eight book lookups clear (step 13 of
[`docs/demo.md`](../docs/demo.md)). Demo sessions do not resume in normal mode, nor the other way round.
`BOOKSHOP_EXPORTS` names another export server's MCP endpoint than `http://localhost:18800/mcp`.
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

Mutation testing, over the core, the test kit, the Claude package and the MCP client; `go-mutation` fails below the thresholds in
[`.gremlins.yaml`](.gremlins.yaml) (G12):

```sh
go install github.com/go-gremlins/gremlins/cmd/gremlins@v0.6.0
# Each mutant is tested in a copy of go/ alone, which finds the shared testdata through OFFICINA_TESTDATA; -count=1
# keeps the coverage run, which sets the mutants' timeouts, out of the test cache.
OFFICINA_TESTDATA=$PWD/../testdata GOFLAGS=-count=1 gremlins unleash --coverpkg ./officina/... --timeout-coefficient 20 ./officina
```

CI is [`.github/workflows/go.yml`](../.github/workflows/go.yml), on every pull request: `go-ubuntu` and `go-windows`
(the tests, with the race detector), `go-quality` (the gates above) and `go-mutation`. Before a commit that stages Go
code, a hook runs `gofmt`, `go vet`, `golangci-lint` and `go build`; before a push that changes Go code, the tests.
