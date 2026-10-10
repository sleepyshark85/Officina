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
| The MCP client (Ruby S11, part A): `Mcp.connect(Mcp::Server, cancel:)` returns a `Client` with `list_tools` (every page), `call_tool` and `close`. A server that cannot be started or reached, fails or goes away raises `Mcp::Error`, and the connection stays lost; one that answers a request with a JSON-RPC error raises it too, but keeps the connection; a tool's own failure is a `CallResult` whose `error?` is true | ruby/CLAUDE.md: a broken server is a broken environment, raised; a failing tool is a result. Part B turns the error into the run failing at connect, or into an error result mid-run (MCP-04) |
| A request waits in 50 ms slices, checking `cancel.cancelled?` and the 30 s connect deadline between; cancel is any `_Cancellation` (a `cancelled?` method) | R10: cooperative, and it needs nothing of the core's `Cancellation`, which Ruby S03 builds in parallel; part B passes the run's. A cancelled call raises without waiting for the server's tool |
| Stdio: the program runs as `[program, program]` (no shell), in a process group of its own on Unix (`new_pgroup` on Windows). One thread reads the output and hands each response to its request's `Thread::Queue`; one keeps the end of the error output, whose last line says why a server ended. `close` closes the input, waits 5 s for the exit, kills the group (`taskkill /T` on Windows), reaps it through `Process.detach`'s thread, and closes a pipe a stray process still holds after 5 s, so every thread ends. A message is written whole; a server that stops reading its input while one longer than the pipe's buffer is written blocks that write, whatever the cancellation, until the server ends | As Go's: a server stops by itself when its input ends, as a container must; the group catches what a launcher (`npx`, `uvx`) started. Every thread is joined (R11) |
| Streamable HTTP: one `Net::HTTP` connection per request, opened and read on a thread of its own, so the caller checks the cancellation and the connect deadline from the start. Cancelling closes the connection, which ends the blocked read with `IOError`, and joins the thread. A connection still opening cannot be closed: a request cancelled then waits for it to open or fail, at most 10 s (the open timeout). No read timeout: the run's cancellation ends a call. A notification waits for its 202 | `Net::HTTP` has no cancel, and `Thread#raise` is ruled out (R10); closing a socket from another thread is how Ruby ends a blocked read. One connection per request leaves no pool to manage |
| JSON-RPC and event-stream parsing (`Wire`, internal) is tested directly with generated streams (`pbt`), reached through `const_get`; a response is a message with a result or error, a positive integer id and no method; anything else a server writes is skipped | TEST-07. Hundreds of cases need no connection; the internals stay `private_constant` |
| Value classes that need methods (`CallResult`, `Server`, `FakeMcpTool`) are `Data.define` and then reopened, not given a block | Steep does not read a `Data.define` block as the class's body. Data's own `initialize`, which `Server`'s `super` reaches with the members, has no signature, so that call is `# steep:ignore`d, like `Process.spawn`'s `[program, argv0]` form and `Net::HTTP`'s nil timeouts |
| The fake MCP server is `Testing::FakeMcpServer`: `#serve` speaks stdio, run by a fixture script the tests start with `--disable-gems`, so it starts in milliseconds; `#serve_http` speaks Streamable HTTP on a `TCPServer`, one request per connection, joined when its block ends | R6: the standard library has no HTTP server. One tool per page, a notification before each result, and the headers and session the protocol asks for, as .NET's fake |

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
| Ownership | Every thread is joined in an `ensure` before the run returns; the thread-leak check fails any test that leaves one running (R11; the check is built). An MCP client's threads and child process are its own: each request's thread ends with the request, and `Client#close` stops and reaps the server and joins its readers |
| Time | A `clock:` callable wherever the time is read, real by default, fake in tests (R12). *Planned (S03)* |
| Composition root | `Bookshop.build`, called by `exe/bookshop` and by the tests with boundary fakes (R5). *Planned (S06)* |
