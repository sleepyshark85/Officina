# Test report

A snapshot of `main` on **2026-10-07** (commit `c6fc554`): what is tested, how well, what gates a change, and what
is left to do. Fresh numbers come from every CI run (tests, coverage) and the weekly mutation run; see
[Reproducing](#reproducing).

## Summary

| Measure | Result |
|---|---|
| Tests | **350**, all passing (347 run in CI on Linux; 3 run only on request) |
| Line / branch coverage | **95.1%** / **91.7%** of Officina's own assemblies |
| Mutation score | core **75.8%**, Claude **83.3%**, MCP **69.2%** |
| Required checks on every pull request | build and tests on Linux and Windows, format, vulnerable packages, mutation |

## Test suites

From CI run [37560093413](https://github.com/sleepyshark85/officina/actions/runs/37560093413).

| Project | Tests | Linux | Windows | What it tests |
|---|---|---|---|---|
| `Sleepyshark.Officina.Tests` | 176 | 176 passed | 176 passed | The core: run loop, tool pipeline, budgets, audit, memory, telemetry, output, schemas; property tests (CsCheck) |
| `Sleepyshark.Officina.Claude.Tests` | 54 | 54 passed | 54 passed | The Claude adapter against a fake HTTP API: request layout, streaming, retries, caching, errors |
| `Sleepyshark.Officina.Mcp.Tests` | 23 | 23 passed | 23 passed | The MCP client over stdio and HTTP against the test kit's fake server |
| `Sleepyshark.Officina.Dependencies.Tests` | 16 | 16 passed | 16 passed | The dependency rules (TEST-05) and the core's size |
| `Samples.Tests` | 8 | 8 passed | 8 passed | The GEN-06 samples with the scripted model |
| `BookshopAssistant.Tests` | 73 | 70 passed, 3 skipped | 15 passed, 58 skipped | The app against PostgreSQL in Docker: tools, console, sessions, memory, exports, audit |

- **Skipped on purpose:** the two live smoke tests (TEST-04: Claude, an API key, about $0.40 a run) and the search
  benchmark, which run with `OFFICINA_LIVE_TESTS=1` or `OFFICINA_BENCHMARK=1`.
- **Windows** skips the tests that need PostgreSQL in Docker (`DatabaseFact`, Linux only).
- **Fakes** replace only system boundaries: the model, the network and MCP servers, the clock, the human, and storage
  where a real one is impractical (docs/conventions.md). Most offline tests take milliseconds, but two property tests take just
  over a second (see [Open gaps](#open-gaps)); the app's tests against PostgreSQL in Docker take up to about 3 s each
  (measured locally with `--logger trx`; CI does not record per-test times).

## Coverage

Lines and branches of Officina's own assemblies that the tests run, from the same CI run (`coverage-report`
artifact). The rates move by about a point between runs, as some branches depend on timing.

| Assembly | Lines | Branches |
|---|---|---|
| `Sleepyshark.Officina` | 98.6% | 95.2% |
| `Sleepyshark.Officina.Claude` | 96.4% | 86.1% |
| `Sleepyshark.Officina.Mcp` | 92.1% | 91.4% |
| `Sleepyshark.Officina.Testing` | 97.8% | 98.9% |
| `BookshopAssistant` | 89.4% | 87.5% |
| **Total** | **95.1%** | **91.7%** |

Coverage shows code ran, not that a test checked it, so it is reported, not gated.

## Mutation testing

Stryker.NET changes the library code on purpose (a `>` to `>=`, a removed statement) and checks that a test fails.
Full run [37519980740](https://github.com/sleepyshark85/officina/actions/runs/37519980740) on `14ab015`
(`mutation-reports` artifact); no `src/` file changed between that commit and `c6fc554`. The score is (killed +
timeout) ÷ (tested + no coverage), where a mutant that crashed the test run (a runtime error) is not counted.

| Package | Mutants tested | Killed | Timeout | Survived | No coverage | Score | A pull request fails below |
|---|---|---|---|---|---|---|---|
| Core | 1,262 | 978 | 7 | 277 | 37 | **75.8%** | 70% |
| Claude | 167 | 144 | 1 | 22 | 7 | **83.3%** | 78% |
| MCP | 205 (1 runtime error) | 148 | 9 | 47 | 23 | **69.2%** | 64% |

A pull request mutates only the files it changes. The core is 98.6% covered but scores 75.8%: most of its lines run,
but about a quarter of changes to them would go unnoticed. Part of that is expected (message wording, equivalent
mutants); the rest is listed below.

## Gates

| Where | Gate |
|---|---|
| Required checks (branch protection) | `ubuntu-latest`, `windows-latest` (build with warnings as errors, all tests), `quality` (format, vulnerable packages), `mutation` (changed files above their package's threshold); the branch up to date and conversations resolved |
| Other checks (not required) | CodeQL for the C# and the workflows; `review`, set by the `reviewer` agent's verdict comment, which docs/conventions.md asks for before a merge |
| Claude Code hooks, blocking (`.claude/`) | No push to `main` or commit on `main`; branch prefixes; staging in its own command before a commit; before a commit that stages code, the format check and Release build; before a push of more than docs, the tests |
| Claude Code hooks, reporting | After a `.cs` edit: one type per file named after it, no requirement IDs in comments (Claude is told to fix it) |

## Open gaps

Surviving mutants and branches worth a test, most important first:

| File | Score | Why it matters |
|---|---|---|
| `Memory/MemoryPath.cs` | 61% (17 survivors) | Validates memory file paths against traversal |
| `Memory/FileMemoryStore.cs` | 43% (20 survivors) | The built-in file store; the core's tests mostly use the in-memory one |
| `Memory/MemoryTool.cs` | 68 undetected (43 not wording) | The memory tool's commands and their error cases |
| `Audit/JsonLinesAuditSink.cs` | 40% | The built-in audit sink must keep every entry |
| `RetryAfterHandler.cs` | 40% | Retry timing on rate limits and overload |
| `McpConnection.cs` | 62% (22 survivors) | JSON-RPC errors and response matching |
| `Output/SchemaValidator.cs` | 49 undetected (17 not wording) | Validates tool input and typed output against their schemas |
| `ClaudePrices.cs` | 0% | No test pins a price; cost budgets depend on them |

Two offline tests break docs/conventions.md's one-second rule and need speeding up:
`MemoryPropertyTests.Memory_paths_never_leave_their_scope` (1.0–1.2 s) and
`ConversationPropertyTests.Any_sequence_of_runs_…` (1.1 s).

Left untested on purpose: killing a stdio MCP server that will not exit (a 5 s wait) and the 30 s connect timeout
(`McpToolSource.ConnectTimeout`, which also bounds the ping), as a test would wait out the real time.

## Reproducing

| What | Where or how |
|---|---|
| Tests | `dotnet test`, in `dotnet/` like every `dotnet` command here |
| Coverage | `dotnet test --collect "Code Coverage" --settings coverage.runsettings --results-directory coverage`; in CI, the Linux job's summary and `coverage-report` artifact |
| Mutation | `cd src/<package> && dotnet stryker`, report in `StrykerOutput/`; in CI, the Mutation workflow's `mutation-reports` artifact (weekly, or run it by hand) |
| Live smoke test | `OFFICINA_LIVE_TESTS=1 dotnet test tests/BookshopAssistant.Tests --filter Category=Live`, with `ANTHROPIC_API_KEY` set |
| Requirements to tests | [traceability.md](traceability.md) |
