# Running and testing the Ruby implementation

How to install, run and test Officina for Ruby and Bookshop Assistant, as `main` has them. Commands run from `ruby/`
unless a block says otherwise. What the code is and why: [`../README.md`](../README.md), [`design.md`](design.md) and
[`docs/implementations/ruby.md`](../../docs/implementations/ruby.md).

## Prerequisites

| Tool | What it is for | .NET counterpart |
|---|---|---|
| [mise](https://mise.jdx.dev) | Installs and selects the Ruby version, 4.0.7 (`ruby/.ruby-version`) | The SDK version in `global.json` |
| Bundler (`bundle`) | Installs the gems in `Gemfile.lock` and runs commands with exactly those (`bundle exec …`) | `dotnet restore` and the central package versions |
| Docker | The compose stack, and the database and export server the application's tests start | The same |

Install Ruby once, then either call it through `mise exec` or put mise's shims on `PATH`:

```sh
mise install ruby@4.0.7
mise use --global ruby@4.0.7                      # the shims' default, as on the owner's machine

mise exec ruby@4.0.7 -- bundle exec rake          # one command with that Ruby, shims or not
export PATH="$HOME/.local/share/mise/shims:$PATH" # or: ruby, bundle and the gems' commands run 4.0.7 from here on
```

The shims run the version mise selects: the global one above. mise reads `.ruby-version` only once told to
(`mise settings add idiomatic_version_file_enable_tools ruby`); the global pin makes that unnecessary while 4.0.7 is
the only Ruby you use.

Then install the gems, and the gem signatures Steep reads:

```sh
bundle install                       # after a clone, and after every pull that changes Gemfile.lock or a gemspec
bundle exec rbs collection install   # into .gem_rbs_collection/ (git-ignored), after the same changes
```

## The compose stack

Bookshop Assistant needs three services, started by one script from the shared
[`bookshop/`](../../bookshop/) folder (the .NET and Go applications use the same stack):

```sh
../bookshop/start.sh
```

It stops with *Docker is not running, or this user cannot reach it.* when Docker is not up; otherwise it runs
`docker compose up --detach --wait --build`, which leaves running services running and recreates any whose definition
changed, and prints the host port each one got.

| Service | What | Default port on this machine | Port variable |
|---|---|---|---|
| `postgres` | PostgreSQL 17: schema, seed, audit and sessions tables, created on its first start | 5432 | `BOOKSHOP_DB_PORT` |
| `dashboard` | The Aspire dashboard: traces, metrics and logs, in memory | 18888 (web), 4318 (OTLP/HTTP, which Ruby sends), 4317 (OTLP/gRPC, .NET's and Go's) | `BOOKSHOP_DASHBOARD_PORT`, `BOOKSHOP_OTLP_HTTP_PORT`, `BOOKSHOP_OTLP_PORT` |
| `filesystem` | The reference filesystem MCP server, over Streamable HTTP at `/mcp`, seeing only `bookshop/exports/`; on 127.0.0.1 only | 18800 | `BOOKSHOP_EXPORTS_PORT` |

When a port is taken, put another in `bookshop/.env` (git-ignored), which Docker Compose reads beside
`compose.yaml`, and point the application's matching setting at it ([Settings](#settings)). Find who holds a port with
`docker ps --format '{{.Names}} {{.Ports}}'` (a container) or `ss -ltnp` (any process). On the owner's machine
another project's PostgreSQL holds 5432 and another project's collector holds 4317 and 4318, so that file sets:

```sh
BOOKSHOP_DB_PORT=5433
BOOKSHOP_OTLP_PORT=4319
BOOKSHOP_OTLP_HTTP_PORT=4320
```

Moving a port without pointing the application at it fails silently: Ruby's default endpoint, `localhost:4318`, sends
its telemetry to the other project's collector, and the dashboard stays empty. `BOOKSHOP_DASHBOARD_PORT` and
`BOOKSHOP_EXPORTS_PORT` move the same way.

`docker compose down -v` (in that folder) deletes the database's volume, so the next start seeds it afresh.

## Settings

The application reads its settings from the environment, and from `ruby/apps/bookshop/.env` if there is one (loaded
by [dotenv](https://github.com/bkeepers/dotenv) in `exe/bookshop`). That file is git-ignored; start it from the
example, which lists every setting commented out at its default, and uncomment what you change:

```sh
cp apps/bookshop/.env.example apps/bookshop/.env
```

A variable set in the shell wins over the file. Every address default is the compose stack's.

| Setting | Default | What it does |
|---|---|---|
| `BOOKSHOP_DATABASE` | `postgres://bookshop:shelf-demo-41@localhost:5432/bookshop` | The PostgreSQL URL. With `BOOKSHOP_DB_PORT=5433`, set it to `postgres://bookshop:shelf-demo-41@localhost:5433/bookshop` |
| `BOOKSHOP_DASHBOARD` | `http://localhost:18888` | The dashboard `/audit` links each run's trace to |
| `BOOKSHOP_EXPORTS` | `http://localhost:18800/mcp` | The export server's MCP endpoint, connected to at the start. Empty: the assistant runs without exports. Not an http or https URL: the start stops with a message |
| `BOOKSHOP_REPLY_BUDGET` | `0.50` | A reply's budget in US dollars; `0.01` shows a budget stop. Not an amount above zero: the start stops with a message. The session's budget, $5, is fixed |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4318` | OpenTelemetry's own setting: where traces, metrics and logs go, over OTLP/HTTP. With `BOOKSHOP_OTLP_HTTP_PORT=4320`, set it to `http://localhost:4320` |
| `ANTHROPIC_API_KEY` | none | The Claude API key, read by the Anthropic SDK |

Keep the API key out of `.env` where you can: set `ANTHROPIC_API_KEY` in the shell, or sign in once with
`ant auth login`, whose credentials the SDK finds when no key is set. A line in `.env` works too.

The assistant's memory of each staff member is kept as files under `data/memory/` in the directory you start it from
(git-ignored).

## Running

### The console app

```sh
../bookshop/start.sh
bundle exec apps/bookshop/exe/bookshop          # Claude Opus 5.5; a reply costs a few cents
bundle exec apps/bookshop/exe/bookshop --demo   # compaction from 50,000 input tokens, clearing above 12 tool calls
```

It asks your name, then takes messages. Each reply streams, with every tool call shown (`> name input`, then
`< name: outcome`), and ends with a status line of its tokens, the share read from the cache, its cost and the
session's. Things to try:

| Try | What you see |
|---|---|
| *Order the two cheapest fantasy books in stock for Alice Martin and tell me the total* | Searches, then `? place_order needs your approval. Its exact input:` and `Approve? [y/N]`; `y` or `yes` approves, anything else declines |
| *Export Alice Martin's order history as CSV* | `? filesystem__write_file needs your approval`; once approved, the file appears in `bookshop/exports/` |
| Ctrl+C during a reply | `[Cancelled.]`; the session goes on. Ctrl+C at the prompt leaves |
| `BOOKSHOP_REPLY_BUDGET=0.01` | The reply stops at its budget and says so |
| `--demo` and a few broad searches | `~ Conversation compacted: …` and `~ Old tool results cleared: …` as they happen |

The commands (`/help` lists them):

| Command | Does |
|---|---|
| `/new` | Starts a new session (the one left is summarized) |
| `/sessions` | Lists the latest sessions with title, summary and changes |
| `/resume <id>` | Goes on with a session, cache intact |
| `/cost` | The session's tokens and cost against its $5 budget |
| `/audit [<id>]` | The audit trail of this session or another, each run with its trace link |
| `/memory` | What the assistant remembers for you |
| `/quit` | Leaves (the session is summarized: at most $0.05) |

### `/audit` and the dashboard

`/audit` prints each run's entries with a link, `<BOOKSHOP_DASHBOARD>/traces/detail/<trace id>`. Open it, or
<http://localhost:18888>, to see the reply as one trace: the reply, its run, each model call and tool call, with its
log record. The dashboard keeps them in memory only, so a restart of its container empties it.

## Testing

### The whole workspace

```sh
bundle exec rake        # RuboCop, then Steep, then the tests: the RuboCop, Steep and test steps of CI's ruby-quality and ruby-ubuntu
bundle exec rake test   # the tests alone
```

The tests run in one process, one test at a time, in random order, with warnings on: a warning about a workspace
file fails the run (a gem's own warning is only printed). They need no API key and no network; with Docker, the
whole suite takes under a minute. `SEED=<n>` repeats Minitest's order, and `COVERAGE=1` writes a SimpleCov report to
`coverage/`.

### One gem, one file, one test

The Rakefile's task always loads every test file; `N` filters by name, as a string or `/regexp/`:

```sh
bundle exec rake test N=/app12/            # every test whose name holds app12, across the workspace
```

To load less, run Minitest's own command with the same load path and helper the Rakefile gives:

```sh
bundle exec ruby -w -Itest -I. -rtest_helper -S minitest officina-mcp/test                    # one gem
bundle exec ruby -w -Itest -I. -rtest_helper officina-mcp/test/stdio_test.rb                  # one file
bundle exec ruby -w -Itest -I. -rtest_helper -S minitest officina-mcp/test/stdio_test.rb:35  # one test, by line
bundle exec ruby -w -Itest -I. -rtest_helper officina-mcp/test/stdio_test.rb \
  -n test_mcp01_a_response_to_no_request_waiting_is_skipped                                    # one test, by name
```

Test names carry their requirement IDs (`test_mcp01_…`), and [`traceability.md`](traceability.md) maps them.

### Property tests and their seed

The property tests ([pbt](https://github.com/ohbarye/pbt)) draw a new seed each run. A failure prints it:

```text
Property failed after 12 test(s)
  seed: 1234567890
  counterexample: …
```

Run them again with the same inputs, then with CI's fixed seed:

```sh
PROPERTY_SEED=1234567890 bundle exec rake test N=/test07/
PROPERTY_SEED=20261010 bundle exec rake test
```

### Tests that need Docker

The application's database and end-to-end tests start their own PostgreSQL (`officina-ruby-bookshop-test-<pid>`) and,
for exports, build and start the export server (`officina-ruby-exports-test-<pid>`), on free ports, independent of
the compose stack; both are removed when the run ends. Without Docker:

| Where | What happens |
|---|---|
| Not Linux (Windows, macOS) | Skipped: *the database tests need Linux and Docker* |
| Linux, no Docker socket (`/var/run/docker.sock`) and no `DOCKER_HOST` | Skipped: *the database tests need Docker, which was not found* |
| Linux, the socket there but the daemon down | They fail: `docker run failed: …` |
| CI (`CI` set) | They run, and fail if Docker is missing |

### RuboCop

```sh
bundle exec rubocop                   # the whole workspace (.rubocop.yml)
bundle exec rubocop apps/bookshop     # one folder or file
bundle exec rubocop -a                # autocorrect what is safe to; review the diff
bundle exec rubocop -A                # safe and unsafe corrections: review each one
```

A cop is never disabled to let code through ([the code quality bar](../../docs/conventions.md#code-quality-bar)).

### Steep

```sh
bundle exec rake steep                                          # what CI and the commit hook run
bundle exec steep check apps/bookshop/lib/bookshop/budgets.rb   # one file
```

`rake steep` also fails when Steep's log has a `FATAL` or `ERROR` line, as Steep exits 0 after skipping a file it
could not check ([Troubleshooting](#steep-fatal-or-error-lines)).

### Mutation testing

[mutant](https://github.com/mbj/mutant) runs from each gem with a `mutant.yml` (`officina`, `officina-claude`,
`officina-mcp`; the application has none), with that gem's tests and CI's property seed:

```sh
cd officina
git fetch origin
PROPERTY_SEED=20261010 bundle exec mutant run --since origin/main    # the methods your branch changed, as on a PR
PROPERTY_SEED=20261010 bundle exec mutant run Sleepyshark::Officina::Budget   # one class
PROPERTY_SEED=20261010 bundle exec mutant run                        # every method, as on main and weekly
```

CI (`ruby-mutation`): on a pull request, any surviving mutant in a changed method fails the job; on a push to `main`,
weekly and on demand, every method, failing below 99% or when a gem's run does not reach its summary.

A survivor (*Alive*) is printed per method: its subject (`Sleepyshark::Officina::X#method:<file>:<line>`), then each
mutation as `evil:<subject>:<id>` with a diff, `-` the original and `+` the mutated code that the tests did not
notice, then the tests mutant ran for it. Either add a test that the mutated code fails, or, if the mutated code is
equivalent, simplify the original until it is not, or exclude the method with a `# mutant:disable -- <why>` comment
the review judges. To read a run again:

```sh
bundle exec mutant session list                          # the runs kept in .mutant/ (git-ignored)
bundle exec mutant session subject Sleepyshark::Officina::Budget#initialize   # one method's survivors, latest run
```

### Live tests

On `main`, the Ruby suite makes no live call: Claude is replaced by a scripted model and the API by a fake one, and
nothing reads an API key. The live smoke test (TEST-04: APP-09 against the compose database, cache reads from the
second call, a forced compaction, about $0.40 a run) is planned for Ruby S13, behind `OFFICINA_LIVE_TESTS=1` as in
.NET ([phase 1 plan](../../docs/plan/phase-1.md)). Until then the live checks are by hand, with an API key:

```sh
bundle exec ruby examples/hello/hello.rb    # a chat; from the second message, the status line shows cache reads
bundle exec apps/bookshop/exe/bookshop      # the application, as in Running
```

### The hooks

The hooks in `.claude/` run for Claude Code's own commands only, not for commands typed in your terminal: before
committing or pushing yourself, run `bundle exec rake`.

| When | Runs, for Ruby | Only if |
|---|---|---|
| After an edit to a `.rb` file | Checks `# frozen_string_literal: true` first and no requirement IDs in comments | The file is under `ruby/` (fixture gems exempt) |
| Before `git commit` | `bundle exec rubocop`, then `bundle exec rake steep`; blocks a staged file that also has unstaged changes, and shows the staged files | The commit stages a file under `ruby/` that is not `.md`, `.html`, `.svg` or `.png` |
| Before `git push` | `bundle exec rake test` | The branch changes, since its merge base with `origin/main`, such a file under `ruby/` or anything in `testdata/` |

They find `bundle` on `PATH`, or else in mise's shims. They also block a commit on `main`, a push to it, a branch
without an allowed prefix, and staging in the same command as the commit.

## Troubleshooting

### The wrong PostgreSQL

`./start.sh` fails because port 5432 is already allocated, or the first reply fails with
`password authentication failed for user "bookshop"`: another project's PostgreSQL holds 5432. Find it, then move
the stack's port:

```sh
docker ps --filter publish=5432 --format '{{.Names}} {{.Ports}}'
echo BOOKSHOP_DB_PORT=5433 >> ../bookshop/.env
../bookshop/start.sh
```

and set `BOOKSHOP_DATABASE=postgres://bookshop:shelf-demo-41@localhost:5433/bookshop` in `apps/bookshop/.env`.

### Docker not running

`./start.sh` says *Docker is not running, or this user cannot reach it.* Start Docker; on Linux, check
`docker info` works without `sudo` (your user in the `docker` group). The tests need the same ([Tests that need
Docker](#tests-that-need-docker)).

### Steep FATAL or ERROR lines

`rake steep` fails with *Steep logged a FATAL or ERROR line (above): files may not have been checked.* Read the line
above it, which names the file:

- First rule out stale gem signatures, as after a pull that changed `Gemfile.lock` or `rbs_collection.lock.yaml`:
  run `bundle exec rbs collection install`, then `bundle exec rake steep` again.
- If the line remains, Steep crashed on a construct in that file. Check the file alone with
  `bundle exec steep check <file>`, and rewrite the construct; `officina/lib/sleepyshark/officina/memory_tool.rb` has one such
  rewrite, with its reason.

### A stale Gemfile.lock

`bundle exec` stops with `Could not find <gem>-<version> in locally installed gems (Bundler::GemNotFound)`: the lock
names gems not installed yet. Run `bundle install` (and `bundle exec rbs collection install`). When you change a
gemspec or the `Gemfile`, `bundle install` updates `Gemfile.lock`: commit it with the change, as a lock that doesn't
match its manifest is a must-fix in review.

### The export server unreachable at start

The application stops before asking your name:

```text
The export server at http://localhost:18800/mcp cannot be used: MCP server filesystem could not be reached: …
If it is not running, start it with ./start.sh (or pwsh -File start.ps1) in bookshop/; or set BOOKSHOP_EXPORTS to its endpoint, or to nothing to go without exports.
```

Run `./start.sh`, set `BOOKSHOP_EXPORTS` to the endpoint it printed, or set it empty to run without exports.

### No traces on the dashboard

Ruby sends OTLP over HTTP, to port 4318 unless you move it. Two causes leave the dashboard empty:

- **Another stack holds the port.** `docker ps --filter publish=4318` (or `ss -ltnp | grep 4318`) names the holder.
  `./start.sh` then fails with *port is already allocated*, and meanwhile the default endpoint sends the telemetry to
  the holder's collector without any error. Before `./start.sh`, move the port and point the application at it. With
  5433 / 4319 / 4320 as the free ports, `bookshop/.env` gets `BOOKSHOP_DB_PORT=5433`,
  `BOOKSHOP_OTLP_PORT=4319` and `BOOKSHOP_OTLP_HTTP_PORT=4320`, and `ruby/apps/bookshop/.env` gets
  `BOOKSHOP_DATABASE=postgres://bookshop:shelf-demo-41@localhost:5433/bookshop` and
  `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4320` (and `BOOKSHOP_EXPORTS` if the export server's port moved too).
- **An old dashboard container.** One started from a `compose.yaml` older than the HTTP port lacks it:
  `docker compose port dashboard 18890` (in `bookshop`) says *no port 18890/tcp*. `./start.sh` recreates
  it with the current file (emptying it).

### A leftover test container

A test run killed before its end (`kill -9`, a closed terminal) leaves its containers running. List and remove
them; the `<pid>` in a name is that of the run that started it, so leave those of a run still going:

```sh
docker ps --filter label=officina-test --format '{{.Names}} {{.Status}}'
docker rm --force officina-ruby-bookshop-test-<pid>
```
