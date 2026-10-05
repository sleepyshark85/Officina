# Officina

A purpose-neutral .NET 10 library for building agentic applications: agents that call models, use tools and follow
control flow. Packages are named under `Sleepyshark.Officina`.

Phase 1 is accepted against one reference application, **Bookshop Assistant**: an interactive console chatbot over
PostgreSQL in Docker.

- [`REQUIREMENTS.md`](REQUIREMENTS.md): what to build, phase 1 and north star.
- [`ARCHITECTURE.md`](ARCHITECTURE.md): concepts, components, contracts and flows.
- [`CLAUDE.md`](CLAUDE.md): working notes and conventions.

- [`docs/plan/phase-1.md`](docs/plan/phase-1.md): the phase 1 slices.

Status: phase 1, slice S06 (Bookshop console).

## Build and test

Needs the .NET 10 SDK. The tests need no API key and no network. The Bookshop Assistant tests run against PostgreSQL in
Docker (Linux only); without Docker they are skipped.

```sh
dotnet build
dotnet test
```

| Path | Holds |
|---|---|
| `src/Sleepyshark.Officina` | The core; depends on the .NET base library only |
| `src/Sleepyshark.Officina.Claude` | The Claude adapter; the only project that may reference the Anthropic SDK |
| `src/Sleepyshark.Officina.Mcp` | The MCP tool source: our own client, over stdio and Streamable HTTP |
| `src/Sleepyshark.Officina.Testing` | The test kit |
| `apps/BookshopAssistant` | The reference application |
| `samples/hello` | A live chat with Claude (needs `ANTHROPIC_API_KEY`); not part of `dotnet test` |
| `tests/BookshopAssistant.Tests` | The application's tools and console flows against the real database (TEST-09) |
| `tests/` | Tests; `Sleepyshark.Officina.Dependencies.Tests` enforces the dependency rules (TEST-05) |

## Run Bookshop Assistant

Needs Docker and `ANTHROPIC_API_KEY`.

```sh
cd apps/BookshopAssistant
docker compose up -d --wait        # BOOKSHOP_DB_PORT=5433 if port 5432 is taken
export BOOKSHOP_CONNECTION_STRING="Host=localhost;Port=5432;Username=bookshop;Password=shelf-demo-41;Database=bookshop"
dotnet run
```

Try: *Order the two cheapest fantasy books in stock for Alice Martin and tell me the total.*

Exports (APP-12) go to `exports/`, through the reference filesystem MCP server, which the application starts in Docker
with `docker compose run`. Try: *Export Alice Martin's order history as CSV.*
