# Officina

A purpose-neutral .NET 10 library for building agentic applications: agents that call models, use tools and follow
control flow. Packages are named under `Sleepyshark.Officina`.

Phase 1 is accepted against one reference application, **Bookshop Assistant**: an interactive console chatbot over
PostgreSQL in Docker.

- [`REQUIREMENTS.md`](REQUIREMENTS.md): what to build, phase 1 and north star.
- [`ARCHITECTURE.md`](ARCHITECTURE.md): concepts, components, contracts and flows.
- [`CLAUDE.md`](CLAUDE.md): working notes and conventions.

- [`docs/plan/phase-1.md`](docs/plan/phase-1.md): the phase 1 slices.

Status: phase 1, skeleton (slice S01).

## Build and test

Needs the .NET 10 SDK. The tests need no API key and no network.

```sh
dotnet build
dotnet test
```

| Path | Holds |
|---|---|
| `src/Sleepyshark.Officina` | The core; depends on the .NET base library only |
| `src/Sleepyshark.Officina.Claude` | The Claude adapter; the only project that may reference the Anthropic SDK |
| `src/Sleepyshark.Officina.Mcp` | The MCP tool source |
| `src/Sleepyshark.Officina.Testing` | The test kit |
| `apps/BookshopAssistant` | The reference application |
| `samples/hello` | A live chat with Claude (needs `ANTHROPIC_API_KEY`); not part of `dotnet test` |
| `tests/` | Tests; `Sleepyshark.Officina.Dependencies.Tests` enforces the dependency rules (TEST-05) |
