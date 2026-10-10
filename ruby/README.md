# Officina for Ruby

The Ruby implementation of Officina: the same requirements ([`REQUIREMENTS.md`](../REQUIREMENTS.md)) and architecture
([`ARCHITECTURE.md`](../ARCHITECTURE.md)) as the .NET one at the repository root and the Go one in [`go/`](../go/),
written as idiomatic Ruby.

Status: planned. See [`docs/plan/phase-1.md`](../docs/plan/phase-1.md) for the slices (the Ruby column),
[`docs/implementations/ruby.md`](../docs/implementations/ruby.md) for the decisions and [`CLAUDE.md`](CLAUDE.md) for
the working rules.

Planned layout (R2, R3):

| Path | Holds |
|---|---|
| `Gemfile`, `Gemfile.lock`, `.ruby-version` | The Bundler workspace and the pinned Ruby (R1) |
| `officina/` | Gem `sleepyshark-officina`, the core: agent, run engine, conversation, tool pipeline, memory, audit, built-in stores |
| `officina-claude/` | Gem `sleepyshark-officina-claude`: the Claude adapter, the only user of the Anthropic SDK |
| `officina-mcp/` | Gem `sleepyshark-officina-mcp`: the MCP client (stdio and Streamable HTTP) |
| `officina-testing/` | Gem `sleepyshark-officina-testing`: scripted model and approver, fake MCP server, prefix stability check |
| `test/dependencies_test.rb` | The dependency check (TEST-05), with fixture gems in `test/fixtures/dependencies/` |
| `apps/bookshop/` | Bookshop Assistant (from Ruby S06), run by `exe/bookshop` |
| `examples/` | `hello`, a live chat; the GEN-06 samples `extraction`, `chat` and `background` |
| `docs/` | Ruby spike notes and traceability |

## Build and test

Needs the Ruby version in `.ruby-version` and Bundler. The tests need no API key and no network; Bookshop
Assistant's tests need Docker on Linux and skip elsewhere. From this directory, once Ruby S01 is merged:

```sh
bundle install
bundle exec rake          # rubocop, steep, tests
```
