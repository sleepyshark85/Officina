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
| `sig/` | The workspace's corrections to Ruby's core signatures, which Steep reads with the gems' own |
| `apps/bookshop/` | Bookshop Assistant (from Ruby S06), run by `exe/bookshop` |
| `examples/` | `hello`, a live chat; the GEN-06 samples `extraction`, `chat` and `background` (planned) |
| `docs/` | Design notes ([`design.md`](docs/design.md): choices, and how Ruby realizes ARCHITECTURE's runtime model), spike notes and traceability |
| `.rubocop.yml`, `Steepfile`, `rbs_collection.yaml`, `Rakefile` | RuboCop, Steep and the gems' signatures it reads (R13, R18); the tasks |

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
report to `coverage/`. Mutation testing of the core runs from its directory:

```sh
cd officina
bundle exec mutant run                # every method, as on a push to main and weekly
bundle exec mutant run --since main   # only the methods changed since main, as on a pull request
```

The hooks in `../.claude/` run RuboCop and Steep before a commit that stages anything under `ruby/` but docs, and
the tests before a push that changes it; they find `bundle` on `PATH`, or else in mise's shims
(`~/.local/share/mise/shims`), which run the Ruby in `.ruby-version`.
