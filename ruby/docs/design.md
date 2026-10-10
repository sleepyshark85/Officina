# Ruby design notes

Choices in the Ruby code that the code alone does not explain. Concepts are in
[`ARCHITECTURE.md`](../../ARCHITECTURE.md); decisions R1…R20 in [`ruby.md`](../../docs/implementations/ruby.md); what
the Anthropic gem can and cannot do in [`spikes/claude-features.md`](spikes/claude-features.md). Each slice adds the
rows for the choices it makes, and fills in its rows of the runtime-model table below.

| Choice | Why |
|---|---|
| Four gems and the application in one Bundler workspace, with one `Gemfile.lock` | R2. Every gem is built and tested against the same versions, and one `bundle install` sets up everything |
| The dependency test reads the code without running it: each gemspec's runtime dependencies, and every `require` in a gem's `lib/` found with Prism. A `require` it cannot read (a computed path) fails it | R4, TEST-05. Loading the code would run it, and a dependency required only on some path would slip through; a computed `require` could hide anything |
| The test kit may depend on `minitest` | Its thread-leak check is a Minitest lifecycle module that asserts; nothing else in the library gems may use a test framework |
| A warning about a file of the workspace fails the tests; one about a gem's file is printed. Done by prepending a module to `Warning.warn`, loaded before anything else | R13. Ruby has no switch that turns warnings into errors; Warning.warn is its documented hook. The pinned `anthropic` gem warns as it loads, which the workspace cannot fix |
| The thread-leak check is a module included in `Minitest::Test` that compares `Thread.list` before setup and after teardown, so tests run one at a time | R11. Ruby has no goroutine-style leak detector; every thread a test starts must be joined before it ends. With parallel tests another test's threads would count |
| Property tests use `pbt`, seeded from `PROPERTY_SEED` when set, and print the seed on failure | R12, TEST-07. The planned `prop_check` and its fallback `rantly` take no seed, so a failure could not be replayed |
| Mutation testing runs from the core's `mutant.yml` with `usage: opensource` | R13. mutant is free only for open-source projects, which the setting declares |
| The core's public API so far is its `VERSION` and `Sleepyshark::Officina::Error < StandardError` | ruby/CLAUDE.md: misuse and a broken environment raise errors of the core's own hierarchy; a run's outcome is a result value (from Ruby S03) |
| Stored blocks are sent through the request's raw `messages` field (`extra_body`), each as a `JSON::Fragment` of its canonical bytes; nilable block fields are read as `block[:field]` (from Ruby S04) | R9, Ruby S02: the gem re-encodes typed messages, and raises when a nilable field set to `nil` is read through its accessor. One raw field, inside the Claude gem only |

## How Ruby realizes the runtime model

Each row of ARCHITECTURE §5.3, and the layers of §3, in Ruby terms. .NET's and Go's design notes hold the same table.
Rows marked *planned* are the decisions the slice in brackets builds; it replaces the mark with what it built.

| ARCHITECTURE element | Ruby |
|---|---|
| Consuming a run | `Agent#run` yields each event to its block and returns the `Result` (R10). *Planned (S03)* |
| Host stops consuming | Leaving the block (`break`, an exception) cancels the run's tools and waits for them in an `ensure` (R10). *Planned (S03)* |
| Asynchronous I/O | Blocking calls on the run's or a tool's thread; every contract takes the run's `Cancellation` (R10, R11). *Planned (S03)* |
| Concurrent reads | A thread per read call, joined before the next write; I/O releases the VM lock, so reads overlap (R11). *Planned (S05)* |
| Hand-over | The pipeline runs on its own thread and pushes events to a `Thread::Queue` the run drains (R11). *Planned (S05)* |
| Cancellation | `Officina::Cancellation`, one per run; a signal trap only pushes to a queue an owned thread reads (R10). *Planned (S03, S06)* |
| One run per conversation | A second run on a conversation in use raises an `Officina::Error`; S03 picks the mechanism. *Planned (S03)* |
| Ownership | Every thread is joined in an `ensure` before the run returns; the thread-leak check fails any test that leaves one running (R11; the check is built) |
| Time | A `clock:` callable wherever the time is read, real by default, fake in tests (R12). *Planned (S03)* |
| Composition root | `Bookshop.build`, called by `exe/bookshop` and by the tests with boundary fakes (R5). *Planned (S06)* |
