# Officina

A purpose-neutral .NET 10 library for building agentic applications: agents that call models, use tools and follow
control flow. Packages are named under `Sleepyshark.Officina`.

Phase 1 is accepted against one reference application, **Bookshop Assistant**: an interactive console chatbot over
PostgreSQL in Docker.

- [`REQUIREMENTS.md`](REQUIREMENTS.md): what to build, phase 1 and north star.
- [`ARCHITECTURE.md`](ARCHITECTURE.md): concepts, components, contracts and flows.
- [`CLAUDE.md`](CLAUDE.md): working notes and conventions.

- [`docs/plan/phase-1.md`](docs/plan/phase-1.md): the phase 1 slices.
- [`docs/traceability.md`](docs/traceability.md): each phase 1 requirement and its tests.

Status: phase 1 complete; see [`docs/demo.md`](docs/demo.md) and [`docs/traceability.md`](docs/traceability.md).

## Build and test

Needs the .NET 10 SDK. The tests need no API key and no network. The Bookshop Assistant tests run against PostgreSQL in
Docker (Linux only); without Docker they are skipped.

```sh
dotnet build
dotnet test
```

The live smoke test (TEST-04) is skipped unless asked for. It drives APP-09 with Claude, checks cache reads and forces a
compaction, against its own database in Docker; it needs `ANTHROPIC_API_KEY` and costs about $0.40 a run:

```sh
OFFICINA_LIVE_TESTS=1 dotnet test tests/BookshopAssistant.Tests --filter Category=Live --logger "console;verbosity=detailed"
```

| Path | Holds |
|---|---|
| `src/Sleepyshark.Officina` | The core; depends on the .NET base library only |
| `src/Sleepyshark.Officina.Claude` | The Claude adapter; the only project that may reference the Anthropic SDK |
| `src/Sleepyshark.Officina.Mcp` | The MCP tool source: our own client, over stdio and Streamable HTTP |
| `src/Sleepyshark.Officina.Memory.Files` | The file-system memory store |
| `src/Sleepyshark.Officina.Testing` | The test kit |
| `apps/BookshopAssistant` | The reference application |
| `samples/hello` | A live chat with Claude (needs `ANTHROPIC_API_KEY`); not part of `dotnet test` |
| `samples/extraction` | GEN-06: a stateless classifier with typed output and no tools |
| `samples/chat-assistant` | GEN-06: a chat assistant with a conversation and memory per user, and a tool |
| `samples/background-agent` | GEN-06: an unattended job with app tools, an MCP server over HTTP, the JSON-lines audit sink and a budget |
| `tests/Samples.Tests` | The GEN-06 samples, offline with the scripted model |
| `tests/BookshopAssistant.Tests` | The application's tools and console flows against the real database (TEST-09) |
| `tests/` | Tests; `Sleepyshark.Officina.Dependencies.Tests` enforces the dependency rules (TEST-05) |

## Run Bookshop Assistant

Needs Docker and `ANTHROPIC_API_KEY`.

```sh
cd apps/BookshopAssistant
docker compose up -d --wait        # BOOKSHOP_DB_PORT=5433 if port 5432 is taken
docker compose --profile mcp pull  # the export server's image, which the application starts itself
export BOOKSHOP_CONNECTION_STRING="Host=localhost;Port=5432;Username=bookshop;Password=shelf-demo-41;Database=bookshop"
dotnet run                         # add -- --demo (or BOOKSHOP_DEMO=1) to compact and clear early (APP-17); sessions of one mode don't resume in the other
```

Try: *Order the two cheapest fantasy books in stock for Alice Martin and tell me the total.* The demo script,
[`docs/demo.md`](docs/demo.md), walks through every capability.

Exports (APP-12) go to `exports/`, through the reference filesystem MCP server, which the application starts in Docker
with `docker compose run`. Try: *Export Alice Martin's order history as CSV.* The server runs as root in its container,
so on Linux the exported files are owned by root: readable, and deletable from the folder, but not editable in place.

The assistant remembers each staff member's preferences across sessions, under `data/memory` (or `$BOOKSHOP_DATA/memory`);
`/memory` shows them. Memory follows the staff member at the counter: a session resumed by someone else uses their
memory, not the memory of the member who started it. Try *I prefer prices with tax*, then ask for a price in a new session.

Traces leave out message text and tool inputs and results; `BOOKSHOP_TELEMETRY_CONTENT=1` puts them in, for debugging.
