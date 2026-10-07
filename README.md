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
- [`docs/test-report.md`](docs/test-report.md): a dated summary of the tests, coverage, mutation scores, gates and open
  gaps.

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

The search benchmark is skipped too. It loads a million books and 200,000 customers into its own database, about a
minute, and prints each search's median time; each must stay under 50 ms:

```sh
OFFICINA_BENCHMARK=1 dotnet test tests/BookshopAssistant.Tests --filter Category=Benchmark --logger "console;verbosity=detailed"
```

Coverage of Officina's own assemblies is measured on every CI run (Linux) and shown in the job summary, with the
Cobertura file as the `coverage-report` artifact; it is a report, not a gate. Locally:
`dotnet test --collect "Code Coverage" --settings coverage.runsettings --results-directory coverage`.

Mutation testing (Stryker.NET) checks that the tests catch changes to the library packages. Each package has a
`stryker-config.json`; run it from the package's folder, and open the HTML report under `StrykerOutput/`:

```sh
dotnet tool restore
cd src/Sleepyshark.Officina
dotnet stryker               # every mutant: about 4 minutes
dotnet stryker --since:main  # or only the files changed since main, as pull requests do
```

| Package | Score (2026-10-07) | A pull request fails below |
|---|---|---|
| `Sleepyshark.Officina` | 76% | 70% |
| `Sleepyshark.Officina.Claude` | 83% | 78% |
| `Sleepyshark.Officina.Mcp` | 69% | 64% |

The `mutation` workflow runs this on every pull request, weekly and on demand, and keeps the reports as an artifact.

| Path | Holds |
|---|---|
| `src/Sleepyshark.Officina` | The core, with built-in file and in-memory memory stores and a JSON-lines audit sink, used only when the host picks them; depends on the .NET base library and the DI abstractions only |
| `src/Sleepyshark.Officina.Claude` | The Claude adapter; the only project that may reference the Anthropic SDK |
| `src/Sleepyshark.Officina.Mcp` | The MCP tool source: our own client, over stdio and Streamable HTTP |
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

Needs Docker and an Anthropic API key: put it in `apps/BookshopAssistant/appsettings.Local.json`, which git ignores,
as `{ "AnthropicApiKey": "sk-ant-…" }`, or sign in with `ant auth login`.

```sh
cd apps/BookshopAssistant
./start.sh                         # or pwsh -File start.ps1
dotnet run                         # add -- --demo to compact and clear early (APP-17); sessions of one mode don't resume in the other
```

All settings are in `appsettings.json`, with a comment on each; `appsettings.Local.json` overrides any of them on this
machine. If a port is taken, set another in a `.env` file beside `compose.yaml`, such as `BOOKSHOP_DB_PORT=5433`, and
change the matching setting (here `Database`) in `appsettings.Local.json`, as `compose.yaml` explains.

Try: *Order the two cheapest fantasy books in stock for Alice Martin and tell me the total.* The demo script,
[`docs/demo.md`](docs/demo.md), walks through every capability.

`start.sh` starts the database, the telemetry dashboard and the export server in Docker, and leaves them running if
they already are.

Exports (APP-12) go to `exports/`, through the reference filesystem MCP server, which runs in Docker behind a bridge
that serves it over Streamable HTTP. Try: *Export Alice Martin's order history as CSV.* The server runs as root in its container,
so on Linux the exported files are owned by root: readable, and deletable from the folder, but not editable in place.

The assistant remembers each staff member's preferences across sessions, under `data/memory` (the `DataFolder` setting);
`/memory` shows them. Memory follows the staff member at the counter: a session resumed by someone else uses their
memory, not the memory of the member who started it. Try *I prefer prices with tax*, then ask for a price in a new session.

Traces leave out message text and tool inputs and results; the `TelemetryContent` setting puts them in, for debugging.
