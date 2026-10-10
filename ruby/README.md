# Officina for Ruby

The Ruby implementation of Officina: the same requirements ([`REQUIREMENTS.md`](../REQUIREMENTS.md)) and architecture
([`ARCHITECTURE.md`](../ARCHITECTURE.md)) as the .NET one at the repository root and the Go one in [`go/`](../go/),
written as idiomatic Ruby.

Status: in progress, from the skeleton (Ruby S01). See [`docs/plan/phase-1.md`](../docs/plan/phase-1.md) for the
slices (the Ruby column), [`docs/implementations/ruby.md`](../docs/implementations/ruby.md) for the decisions,
[`CLAUDE.md`](CLAUDE.md) for the working rules and [`docs/traceability.md`](docs/traceability.md) for the tests of
each requirement.

Layout (R2, R3); each gem has `lib/`, `sig/` (its RBS signatures), `test/` and its gemspec:

| Path | Holds |
|---|---|
| `Gemfile`, `Gemfile.lock`, `.ruby-version` | The Bundler workspace and the pinned Ruby (R1) |
| `officina/` | Gem `sleepyshark-officina`, the core: agent, run engine, conversation, tool pipeline, memory, audit, built-in stores |
| `officina-claude/` | Gem `sleepyshark-officina-claude`: the Claude adapter, the only user of the Anthropic SDK |
| `officina-mcp/` | Gem `sleepyshark-officina-mcp`: the MCP client (stdio and Streamable HTTP) |
| `officina-testing/` | Gem `sleepyshark-officina-testing`: the thread-leak check every test runs; scripted model and approver, fake MCP server, prefix stability check |
| `test/` | `test_helper.rb`, which every test loads first (with `workspace_warnings.rb`: a warning about a workspace file fails the tests, a gem's is printed), the dependency check `dependencies_test.rb` (TEST-05), with fixture gems in `test/fixtures/dependencies/`, and the core's line budget `core_budget_test.rb` (R14) |
| `sig/` | The workspace's corrections to Ruby's core signatures, and the few signatures of the `anthropic` gem and `JSON::Fragment` that the Claude gem calls, which Steep reads with the gems' own |
| `apps/bookshop/` | Bookshop Assistant (from Ruby S06), run by `exe/bookshop` |
| `examples/` | `hello`, a live chat on Claude (with its test on a fake API); the GEN-06 samples `extraction`, `chat` and `background` (planned) |
| `docs/` | Design notes ([`design.md`](docs/design.md): choices, and how Ruby realizes ARCHITECTURE's runtime model), spike notes and traceability |
| `.rubocop.yml` (with `.rubocop_tests.yml`, which each `test/` inherits), `Steepfile`, `rbs_collection.yaml`, `Rakefile` | RuboCop, Steep and the gems' signatures it reads (R13, R18); the tasks |

## Build and test

Needs the Ruby version in `.ruby-version` and Bundler. The tests need no API key and no network; Bookshop
Assistant's tests (from Ruby S06) need Docker on Linux and skip elsewhere. From this directory:

```sh
bundle install
bundle exec rbs collection install   # the gems' signatures Steep reads, once and after Gemfile.lock changes
bundle exec rake                     # rubocop, steep, then the tests
bundle exec rake test                # the tests alone: one process, one test at a time, random order
```

A property test that fails prints its seed; `PROPERTY_SEED=<seed> bundle exec rake test` runs it again with the same
inputs (CI always uses one fixed seed). `SEED=<n>` repeats Minitest's test order. `COVERAGE=1` writes a SimpleCov
report to `coverage/`. Mutation testing runs from the directory of each gem with a `mutant.yml` (`officina`,
`officina-claude`, `officina-mcp`), with CI's property seed:

```sh
cd officina
PROPERTY_SEED=20261010 bundle exec mutant run               # every method, as on a push to main and weekly
PROPERTY_SEED=20261010 bundle exec mutant run --since main  # the methods changed since main, as on a pull request
```

The `hello` sample chats live with Claude Opus 5.5 and shows each call's tokens, cache reads from the second message
on. It needs an API key in `ANTHROPIC_API_KEY`; the tests run it on a fake API instead:

```sh
bundle exec ruby examples/hello/hello.rb
```

## Bookshop Assistant

The reference application: a console chatbot for bookshop staff over the same PostgreSQL database as the .NET and Go
ones, from the compose file, schema and seed in [`apps/BookshopAssistant/`](../apps/BookshopAssistant/). It needs
Docker and `ANTHROPIC_API_KEY`; a reply costs a few cents. From this directory:

```sh
(cd ../apps/BookshopAssistant && ./start.sh)
bundle exec apps/bookshop/exe/bookshop
```

It asks your name, then takes messages, such as *Order the two cheapest fantasy books in stock for Alice Martin and
tell me the total*. Replies stream with each tool call shown; a change asks for your approval with its exact input.
Ctrl+C stops a reply in progress, and the session goes on; `/help` lists the commands, `/quit` leaves.
Each conversation is a session, saved in the database's `sessions` table after every step of a reply, so a crash
loses at most the step in flight: `/sessions` lists the latest, `/resume <id>` goes on with one with its cache intact
(a call a crash left unanswered is told to the model as interrupted), `/new` starts another. After each reply a status
line shows its tokens, the share read from the cache, its cost and the session's; `/cost` shows the session's. A
reply may spend $0.50 and a session $5; reaching either stops the reply and says why. `BOOKSHOP_REPLY_BUDGET`, in US
dollars, such as `0.01`, lowers the reply's budget to show a stop; a value that is not an amount above zero stops the
start with a message.
`--demo` (`bundle exec apps/bookshop/exe/bookshop --demo`) compacts the conversation from 50,000 input tokens and
clears old tool results above 12 tool calls, so a short session shows both; the console says when each happens.
`/audit` shows the session's audit trail, each run with a link to its trace on the compose file's telemetry dashboard,
<http://localhost:18888>, where each reply is one trace (the reply, its run, model calls and tool calls) with its log
record; the application sends its traces, metrics and logs there over OTLP/HTTP, port 4318.
`BOOKSHOP_DATABASE`, a PostgreSQL URL, names another database than the compose file's, such as one on another port:
`postgres://bookshop:shelf-demo-41@localhost:5433/bookshop`; `BOOKSHOP_DASHBOARD` another dashboard for `/audit`'s
links, and OpenTelemetry's own `OTEL_EXPORTER_OTLP_ENDPOINT` another OTLP/HTTP endpoint, such as
`http://localhost:4328`.

The hooks in `../.claude/` run RuboCop and Steep before a commit that stages anything under `ruby/` but docs, and
the tests before a push that changes it; they find `bundle` on `PATH`, or else in mise's shims
(`~/.local/share/mise/shims`), which run the Ruby in `.ruby-version`.
